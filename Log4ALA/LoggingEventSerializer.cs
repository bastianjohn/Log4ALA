﻿//--------------------------------------------------------------
// Copyright (c) 2016 PTV Group
// 
// For license details, please refer to the file LICENSE, which 
// should have been provided with this distribution.
//--------------------------------------------------------------

using log4net.Core;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Dynamic;

using System.Linq;
using System.Text;
namespace Log4ALA
{
    public class LoggingEventSerializer
    {
        public static char[] AllowedCharPlus = new char[] {'_'};

        public string SerializeLoggingEvents(IEnumerable<LoggingEvent> loggingEvents)
        {
            var sb = new StringBuilder();

            foreach (var loggingEvent in loggingEvents)
            {
                sb.AppendLine(SerializeLoggingEvent(loggingEvent));
            }

            return sb.ToString();
        }

        private string SerializeLoggingEvent(LoggingEvent loggingEvent)
        {


            dynamic payload = new ExpandoObject();
            payload.DateValue = loggingEvent.TimeStamp.ToUniversalTime().ToString("o");
            payload.LogMessage = loggingEvent.MessageObject;
            payload.Logger = loggingEvent.LoggerName;
            payload.Level = loggingEvent.Level.DisplayName.ToUpper();

            //If any custom properties exist, add them to the dynamic object
            //i.e. if someone added loggingEvent.Properties["xx:traceId"] = "helloWorld"
            foreach (var key in loggingEvent.Properties.GetKeys())
            {
                ((IDictionary<string, object>)payload)[RemoveSpecialCharacters(key)] = loggingEvent.Properties[key];
            }

            var exception = loggingEvent.ExceptionObject;
            if (exception != null)
            {
                Log4ALAAppender.Error($"loggingEvent.Exception: {exception}");
                payload.exception = new ExpandoObject();
                payload.exception.message = exception.Message;
                payload.exception.type = exception.GetType().Name;
                payload.exception.stackTrace = exception.StackTrace;
                if (exception.InnerException != null)
                {
                    payload.exception.innerException = new ExpandoObject();
                    payload.exception.innerException.message = exception.InnerException.Message;
                    payload.exception.innerException.type = exception.InnerException.GetType().Name;
                    payload.exception.innerException.stackTrace = exception.InnerException.StackTrace;
                }
            }

            return JsonConvert.SerializeObject(payload, Formatting.None);
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
    }
}
