﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Log4ALA
{
    public class QueueLogger
    {
        // Error message displayed when queue overflow occurs. 
        protected const String QueueOverflowMessage = "\n\nAzure Log Analytics buffer queue overflow. Message dropped.\n\n";

        protected readonly BlockingCollection<string> Queue;
        protected readonly Thread WorkerThread;
        protected readonly Random Random = new Random();

        protected bool IsRunning = false;

        private byte[] SharedKeyBytes { get; set; }

        private Log4ALAAppender appender;

        private Timer logQueueSizeTimer = null;

        private AlaTcpClient alaClient = null;
        private CancellationTokenSource tokenSource;
        private CancellationToken cToken;
        private ManualResetEvent manualResetEvent;

        public QueueLogger(Log4ALAAppender appender)
        {
            this.tokenSource = new CancellationTokenSource();
            this.cToken = tokenSource.Token;
            this.manualResetEvent = new ManualResetEvent(false);
            this.appender = appender;
            Queue = new BlockingCollection<string>(appender.LoggingQueueSize != null && appender.LoggingQueueSize > 0 ? (int)appender.LoggingQueueSize : ConfigSettings.DEFAULT_LOGGER_QUEUE_SIZE);
            SharedKeyBytes = Convert.FromBase64String(appender.SharedKey);

            WorkerThread = new Thread(new ThreadStart(Run));
            WorkerThread.Name = $"Azure Log Analytics Log4net Appender ({appender.Name})";
            WorkerThread.IsBackground = true;
            WorkerThread.Priority = (ThreadPriority)Enum.Parse(typeof(ThreadPriority), appender.ThreadPriority);
            if (ConfigSettings.IsLogQueueSizeInterval)
            {
                CreateLogQueueSizeTimer();
            }
        }

        private void CreateLogQueueSizeTimer()
        {
            if (logQueueSizeTimer != null)
            {
                logQueueSizeTimer.Dispose();
                logQueueSizeTimer = null;
            }
            //create scheduler to log queue size to Azure Log Analytics start after 10 seconds and then log size each (2 minutes default)
            logQueueSizeTimer = new Timer(new TimerCallback(LogQueueSize), this, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(ConfigSettings.LogQueueSizeInterval));
        }

        private void LogQueueSize(object state)
        {
            try
            {
                QueueLogger queueLogger = (QueueLogger)state;
                string message = $"{queueLogger.appender.Name}-Size={queueLogger.Queue.Count}";
                queueLogger.appender.log.Inf(message, queueLogger.appender.LogMessageToFile);

                HttpRequest($"{{\"Msg\":\"{message}\",\"{appender.coreFields.DateFieldName}\":\"{DateTime.UtcNow.ToString("o")}\"}}");

            }
            catch (Exception)
            {
                //continue
            }
        }

        private Stopwatch stopwatch = Stopwatch.StartNew();
        private StringBuilder buffer = new StringBuilder();//StringBuilderCache.Acquire();

        protected virtual void Run()
        {
            try
            {

                // Was cancellation already requested by AbortWorker?
                if (this.cToken.IsCancellationRequested == true)
                {
                    appender.log.Inf($"[{appender.Name}] was cancelled before it got started.");
                    cToken.ThrowIfCancellationRequested();
                }


                Connect(true);

                int qReadTimeout = (int)appender.QueueReadTimeout;

                var batchSizeInBytesOrig = appender.BatchSizeInBytes;
                var batchWaitMaxInSecOrig = appender.BatchWaitMaxInSec;
                var batchNumItemsOrig = appender.BatchNumItems;
                var batchWaitInSecOrig = appender.BatchWaitInSec;

                // Send data in queue.
                while (true)
                {

                    buffer.Clear();

                    // Take data from queue.
                    string line = string.Empty;
                    int byteLength = 0;
                    int numItems = 0;
                    buffer.Append('[');
                    stopwatch.Restart();

                    while (((byteLength < appender.BatchSizeInBytes && (stopwatch.ElapsedMilliseconds / 1000) < appender.BatchWaitMaxInSec) ||
                           (numItems < appender.BatchNumItems && byteLength < ConfigSettings.BATCH_SIZE_MAX && (stopwatch.ElapsedMilliseconds / 1000) < appender.BatchWaitMaxInSec) ||
                           ((stopwatch.ElapsedMilliseconds / 1000) < appender.BatchWaitInSec && byteLength < ConfigSettings.BATCH_SIZE_MAX))
                          )
                    {
                        try
                        {
                            if (Queue.TryTake(out line, qReadTimeout))
                            {
                                byteLength += System.Text.Encoding.Unicode.GetByteCount(line);

                                if (numItems >= 1)
                                {
                                    buffer.Append(',');
                                }

                                buffer.Append(line);
                                ++numItems;
                                line = string.Empty;

                            }

                            if (Queue.IsCompleted)
                            {
                                break;
                            }

                        }
                        catch (Exception ee)
                        {
                            if (Queue.IsCompleted)
                            {
                                break;
                            }

                            if (this.cToken.IsCancellationRequested != true) {
                                string errMessage = $"[{appender.Name}] - Azure Log Analytics problems take log message from queue: {ee.Message}";
                                appender.log.Err(errMessage);
                            }
                        }

                    }

                    buffer.Append(']');

                    var alaPayLoad = buffer.ToString();

                    if (alaPayLoad.Length <= 1 || alaPayLoad.Equals("[]"))
                    {
                        string infoMessage = $"[{appender.Name}] -  {nameof(appender.BatchWaitMaxInSec)} exceeded time out of {appender.BatchWaitMaxInSec} seconds there is no data to write to Azure Log Analytics at the moment";
                        appender.log.Inf(infoMessage, appender.LogMessageToFile);
                        continue;
                    }

                    HttpRequest(alaPayLoad);

                    //stop loop if background worker thread was canceled by AbortWorker
                    if (this.cToken.IsCancellationRequested == true)
                    {
                        break;
                    }

                }

            }
            catch (ThreadInterruptedException ex)
            {
                string errMessage = $"[{appender.Name}] - Azure Log Analytics HTTP Data Collector API client was interrupted. {ex}";
                System.Console.WriteLine(errMessage);
                appender.log.Err(errMessage);
                appender.extraLog.Err(errMessage);
            }
        }


        private static IEnumerable<JToken> AllTokens(JObject obj)
        {
            var toSearch = new Stack<JToken>(obj.Children());
            while (toSearch.Count > 0)
            {
                var inspected = toSearch.Pop();
                yield return inspected;
                foreach (var child in inspected)
                {
                    toSearch.Push(child);
                }
            }
        }

        protected void Connect(bool init = false)
        {
            CloseConnection();

            var rootDelay = ConfigSettings.MIN_DELAY;
            int retryCount = 0;

            while (true)
            {
                try
                {
                    OpenConnection();
                    try
                    {
                        appender.log.Inf($"[{appender.Name}] - successfully {(init ? "connected" : "reconnected")} to AlaClient", true);
                    }
                    catch (Exception)
                    {
                        //continue
                    }
                    break;
                }
                catch (Exception ex)
                {
                    CloseConnection();
                    string errMessage = $"[{appender.Name}] - Unable to {(init ? "connect" : "reconnect")} to AlaClient => [{ex.Message}] retry [{(retryCount + 1)}]";
                    appender.log.Err(errMessage);
                    appender.extraLog.Err(errMessage);
                }

                rootDelay *= 2;
                if (rootDelay > ConfigSettings.MAX_DELAY)
                    rootDelay = ConfigSettings.MAX_DELAY;

                var waitFor = rootDelay + Random.Next(rootDelay);

                ++retryCount;

                try
                {
                    Thread.Sleep(waitFor);
                }
                catch (Exception ex)
                {
                    string errMessage = $"[{appender.Name}] - Thread sleep exception => [{ex}]";
                    appender.log.Err(errMessage);
                    throw new ThreadInterruptedException();
                }
            }
        }

        protected virtual void OpenConnection()
        {
            try
            {
                // Create AlaClient instance providing all needed parameters.
                alaClient = new AlaTcpClient(appender.SharedKey, appender.WorkspaceId); //, (bool)appender.UseSocketPool, (int)appender.MinSocketConn, (int)appender.MaxSocketConn);

                alaClient.Connect();

            }
            catch (Exception ex)
            {
                throw new IOException($"An error occurred while init AlaTcpClient.", ex);
            }
        }

        protected virtual void CloseConnection()
        {
            if (alaClient != null)
            {
                alaClient.Close();
            }
        }

        public virtual void AddLine(string line)
        {
            if (!IsRunning)
            {
                WorkerThread.Start();
                IsRunning = true;
            }


            // Try to append data to queue.
            if (!Queue.IsCompleted && !Queue.TryAdd(line))
            {
                if (!Queue.TryAdd(line))
                {
                    appender.log.War($"[{appender.Name}] - QueueOverflowMessage");
                }
            }
        }

        public void AbortWorker()
        {
            if(WorkerThread != null)
            {
                //controlled cancelation of the background worker thread to trigger sending 
                //the queued data to ALA before abort the thread
                this.tokenSource.Cancel();

                Queue.CompleteAdding();
  
                //wait until the worker thread has flushed the locally queued log data
                //and has successfully sent the log data to Azur Log Analytics by HttpRequest(string log) or if
                //the timeout of 10 seconds reached
                manualResetEvent.WaitOne(TimeSpan.FromSeconds(ConfigSettings.AbortTimeoutSeconds));
            }
        }



        private StringBuilder headerBuilder = new StringBuilder();

        private void HttpRequest(string log)
        {

            while (true)
            {
                try
                {
                    headerBuilder.Clear();

                    string result = string.Empty;

                    var utf8Encoding = new UTF8Encoding();
                    Byte[] content = utf8Encoding.GetBytes(log);

                    var rfcDate = DateTime.Now.ToUniversalTime().ToString("r");
                    var signature = HashSignature("POST", content.Length, "application/json", rfcDate, "/api/logs");

                    string alaServerAddr = $"{appender.WorkspaceId}.ods.opinsights.azure.com";
                    string alaServerContext = $"/api/logs?api-version={appender.AzureApiVersion}";

                    // Send request headers
                    headerBuilder.AppendLine($"POST {alaServerContext} HTTP/1.1");
                    headerBuilder.AppendLine($"Host: {alaServerAddr}");
                    headerBuilder.AppendLine($"Content-Length: " + content.Length);   // only for POST request
                    headerBuilder.AppendLine("Content-Type: application/json");
                    headerBuilder.AppendLine($"Log-Type: {appender.LogType}");
                    headerBuilder.AppendLine($"x-ms-date: {rfcDate}");
                    headerBuilder.AppendLine($"Authorization: {signature}");
                    headerBuilder.AppendLine($"time-generated-field: {appender.coreFields.DateFieldName}");
                    headerBuilder.AppendLine("Connection: close");
                    headerBuilder.AppendLine();
                    var header = Encoding.ASCII.GetBytes(headerBuilder.ToString());

                    // Send http headers
                    alaClient.Write(header, 0, header.Length, true);

                    // Send payload data
                    string httpResultBody = alaClient.Write(content, 0, content.Length);


                    if (!string.IsNullOrWhiteSpace(httpResultBody))
                    {
                        string errMessage = httpResultBody;
                        appender.log.Err(errMessage);
                        throw new Exception(errMessage);
                    }
                    alaClient.Put();

                    try
                    {
                        //no loggings in case of LogManager.Shutdown() -> AbortWorker;
                        appender.log.Inf($"[{appender.Name}] - {log}", appender.LogMessageToFile);
                    }
                    catch
                    {
                        //continue
                    }

                }
                catch (Exception ex)
                {
                    // Reopen the lost connection.
                    string errMessage = $"[{appender.Name}] - reopen lost connection. [{ex.Message}]";
                    appender.log.War(errMessage);
                    System.Console.WriteLine(errMessage);

                    Connect();
                    continue;
                }

                //unblock AbortWorker if AbortWorker has canceld the background worker thread
                if (this.cToken.IsCancellationRequested == true)
                {
                    this.manualResetEvent.Set();
                }

                break;
            }

        }


        /// <summary>
        /// SHA256 signature hash
        /// </summary>
        /// <returns></returns>
        private string HashSignature(string method, int contentLength, string contentType, string date, string resource)
        {
            var stringtoHash = method + "\n" + contentLength + "\n" + contentType + "\nx-ms-date:" + date + "\n" + resource;
            var encoding = new System.Text.ASCIIEncoding();
            var bytesToHash = encoding.GetBytes(stringtoHash);
            using (var sha256 = new HMACSHA256(SharedKeyBytes))
            {
                var calculatedHash = sha256.ComputeHash(bytesToHash);
                var stringHash = Convert.ToBase64String(calculatedHash);
                return "SharedKey " + appender.WorkspaceId + ":" + stringHash;
            }
        }

    }

}
