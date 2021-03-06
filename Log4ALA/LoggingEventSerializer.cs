﻿//--------------------------------------------------------------
// Copyright (c) 2016 PTV Group
// 
// For license details, please refer to the file LICENSE, which 
// should have been provided with this distribution.
//--------------------------------------------------------------

using log4net.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace Log4ALA
{
    public class LoggingEventSerializer
    {
        public static char[] AllowedCharPlus = new char[] { '_' };

        public string SerializeLoggingEvents(IEnumerable<LoggingEvent> loggingEvents, Log4ALAAppender appender)
        {
            var sb = new StringBuilder();

            foreach (var loggingEvent in loggingEvents)
            {
                sb.AppendLine(SerializeLoggingEvent(loggingEvent, appender));
            }

            return sb.ToString();
        }

        private string SerializeLoggingEvent(LoggingEvent loggingEvent, Log4ALAAppender appender)
        {

            IDictionary<string, Object> payload = new ExpandoObject() as IDictionary<string, Object>;

            payload.Add(appender.coreFields.DateFieldName, loggingEvent.TimeStamp.ToUniversalTime().ToString("o"));


            var valObjects = new ExpandoObject() as IDictionary<string, Object>;
            if ((bool)appender.JsonDetection && typeof(System.String).IsInstanceOfType(loggingEvent.MessageObject) && !string.IsNullOrWhiteSpace((string)loggingEvent.MessageObject) && ValidateJSON((string)loggingEvent.MessageObject))
            {
                Dictionary<string, string> values = JsonConvert.DeserializeObject<Dictionary<string, string>>((string)loggingEvent.MessageObject);
                foreach (var val in values)
                {
                    if (!valObjects.ContainsKey(val.Key))
                    {
                        payload.Add(val.Key.TrimFieldName((int)appender.MaxFieldNameLength), val.Value.TypeConvert((int)appender.MaxFieldByteLength));
                    }
                }
            }
            else
            {
                if ((bool)appender.KeyValueDetection && typeof(System.String).IsInstanceOfType(loggingEvent.MessageObject) && !string.IsNullOrWhiteSpace((string)loggingEvent.MessageObject) && !ValidateJSON((string)loggingEvent.MessageObject))
                {
                    ConvertKeyValueMessage(payload, (string)loggingEvent.MessageObject, (int)appender.MaxFieldByteLength, appender.coreFields.MiscMessageFieldName, (int)appender.MaxFieldNameLength);
                }
                else
                {
                    payload.Add(appender.coreFields.MiscMessageFieldName, loggingEvent.MessageObject);
                }
            }

            if ((bool)appender.AppendLogger)
            {
                payload.Add(appender.coreFields.LoggerFieldName, loggingEvent.LoggerName);
            }
            if ((bool)appender.AppendLogLevel)
            {
                payload.Add(appender.coreFields.LevelFieldName, loggingEvent.Level.DisplayName.ToUpper());
            }

            //If any custom properties exist, add them to the dynamic object
            //i.e. if someone added loggingEvent.Properties["xx:traceId"] = "helloWorld"
            foreach (var key in loggingEvent.Properties.GetKeys())
            {
                ((IDictionary<string, object>)payload)[RemoveSpecialCharacters(key)] = loggingEvent.Properties[key];
            }

            var exception = loggingEvent.ExceptionObject;
            if (exception != null)
            {
                string errMessage = $"loggingEvent.Exception: {exception}";
                appender.log.Err(errMessage);
                appender.extraLog.Err(errMessage);
                payload.Add("ExMsg", exception.Message);
                payload.Add("ExType", exception.GetType().Name);
                payload.Add("ExStackTrace", exception.StackTrace);
                if (exception.InnerException != null)
                {
                    payload.Add("InnerExMsg", exception.InnerException.Message);
                    payload.Add("InnerExType", exception.InnerException.GetType().Name);
                    payload.Add("InnerExStackTrace", exception.InnerException.StackTrace);
                }
            }

            return JsonConvert.SerializeObject(payload, Formatting.None);
        }

        private static void ConvertKeyValueMessage(IDictionary<string, Object> payload, string message, int maxByteLength, string miscMsgFieldName = ConfigSettings.DEFAULT_MISC_MSG_FIELD_NAME, int maxFieldNameLength = ConfigSettings.DEFAULT_MAX_FIELD_NAME_LENGTH)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                string[] le1Sp = message.Split(';');

                //remove empty objects
                le1Sp = le1Sp.Where(ll => !string.IsNullOrWhiteSpace((ll))).Select(l => l.Trim()).ToArray();

                StringBuilder misc = new StringBuilder();

                ConcurrentDictionary<string, int> duplicates = new ConcurrentDictionary<string, int>();

                foreach (var le1p in le1Sp)
                {
                    if (le1p.Count(c => c == '=') > 1)
                    {
                        string[] le1pSP = le1p.Split(' ');
                        //remove whitespaces
                        le1pSP = le1pSP.Select(l => l.Trim()).ToArray();
                        foreach (var le1pp in le1pSP)
                        {
                            if (le1pp.Count(c => c == '=') == 1)
                            {
                                string[] le1ppSP = le1pp.Split('=');
                                if (!string.IsNullOrWhiteSpace(le1ppSP[0]) && le1ppSP.Length == 2)
                                {
                                    CreateAlaField(payload, duplicates, le1ppSP[0], le1ppSP[1].TypeConvert(maxByteLength), maxFieldNameLength);
                                }
                            }
                            else if(le1pp.Count(c => c == '=') == 2) {
                                string[] le1ppSP = le1pp.Split('=');
                                if(le1ppSP.Length == 3 && !string.IsNullOrWhiteSpace(le1ppSP[0]) && !string.IsNullOrWhiteSpace(le1ppSP[1]) && 
                                    string.IsNullOrWhiteSpace(le1ppSP[2]) && le1pp.Trim().EndsWith("=") && 
                                    $"{le1ppSP[1].Trim()}=".IsBase64())
                                {
                                    CreateAlaField(payload, duplicates, le1ppSP[0], $"{le1ppSP[1].Trim()}=".TypeConvert(maxByteLength), maxFieldNameLength);

                                }
                            }
                            else
                            {
                                misc.Append(le1pp.TypeConvert(maxByteLength));
                                misc.Append(" ");
                            }
                        }
                    }
                    else
                    {
                        if (le1p.Count(c => c == '=') == 1)
                        {
                            string[] le1ppSP = le1p.Split('=');

                            if (!string.IsNullOrWhiteSpace(le1ppSP[0]) && le1ppSP.Length == 2)
                            {
                                CreateAlaField(payload, duplicates, le1ppSP[0], le1ppSP[1].TypeConvert(maxByteLength), maxFieldNameLength);
                            }
                        }
                        else
                        {
                            misc.Append(le1p.TypeConvert(maxByteLength));
                            misc.Append(" ");
                        }
                    }
                }

                string miscStr = misc.ToString().Trim();

                if (!string.IsNullOrWhiteSpace(miscStr))
                {
                    payload.Add(miscMsgFieldName, miscStr.OfMaxBytes(maxByteLength));
                }

            }
        }

        private static void CreateAlaField(IDictionary<string, object> payload, ConcurrentDictionary<string, int> duplicates, string key, object value, int maxFieldNameLength)
        {

            key = key.TrimFieldName(maxFieldNameLength);

            if (!payload.ContainsKey(key))
            {
                payload.Add(key, value);
            }
            else
            {
                int duplicateCounter;
                if (duplicates.ContainsKey(key))
                {
                    duplicates.TryRemove(key, out duplicateCounter);
                    duplicates.TryAdd(key, ++duplicateCounter);
                }
                else
                {
                    duplicateCounter = 0;
                    duplicates.TryAdd(key, duplicateCounter);
                }

                payload.Add($"{key}_Duplicate{duplicateCounter}", value);

            }
        }



        private static string RemoveSpecialCharacters(string str)
        {
            var sb = new StringBuilder(str.Length);
            foreach (var c in str.Where(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || AllowedCharPlus.Any(ch => ch.Equals(c))))
            {
                sb.Append(c);
            }
            return sb.ToString();
        }

        public bool ValidateJSON(string s)
        {
            try
            {
                JToken.Parse(s);
                return true;
            }
            catch (JsonReaderException ex)
            {
                return false;
            }
        }

    }


    static class StringExtension
    {

        public static string OfMaxBytes(this string str, int maxByteLength)
        {
            return str.Aggregate("", (s, c) =>
            {
                if (Encoding.UTF8.GetByteCount(s + c) <= maxByteLength)
                {
                    s += c;
                }
                return s;
            });
        }

        public static string TrimFieldName(this string str, int length = ConfigSettings.DEFAULT_MAX_FIELD_NAME_LENGTH)
        {
            return str.Length > length ? str.Substring(0, length) : str;
        }

        public static bool IsBase64(this string base64value)
        {
            try
            {
                byte[] converted = System.Convert.FromBase64String(base64value);
                return base64value.EndsWith("=");
            }
            catch
            {
                return false;
            }
        }

        public static object TypeConvert(this string messageValue, int maxByteLength)
        {
            string value = messageValue;
            object convertedValue = null;
            DateTime parsedDateTime;
            double parsedDouble;
            bool parsedBool;
            if (Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedDouble))
            {
                convertedValue = parsedDouble;
            }
            else if (DateTime.TryParse(value, out parsedDateTime))
            {
                convertedValue = parsedDateTime.ToUniversalTime();
            }
            else if (Boolean.TryParse(value, out parsedBool))
            {
                convertedValue = parsedBool;
            }
            else
            {
                convertedValue = messageValue.OfMaxBytes(maxByteLength).TrimEnd(new char[] { ',' });
            }

            return convertedValue;
        }


    }
}
