//
// Copyright (c) 2004-2025 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
//
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
// * Redistributions of source code must retain the above copyright notice,
//   this list of conditions and the following disclaimer.
//
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
// * Neither the name of Jaroslaw Kowalski nor the names of its
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//

namespace NLog.Targets
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Globalization;
    using System.ServiceModel;
    using System.ServiceModel.Channels;
    using NLog.Common;
    using NLog.Config;
    using NLog.Layouts;
    using NLog.LogReceiverService;
    using LogEventInfoBuffer = Wcf.LogEventInfoBuffer;

    /// <summary>
    /// Sends log messages to a NLog Receiver Service (using WCF or Web Services).
    /// </summary>
    /// <seealso href="https://github.com/nlog/nlog/wiki/LogReceiverService-target">Documentation on NLog Wiki</seealso>
    [Target("LogReceiverService")]
    public class LogReceiverWebServiceTarget : Target
    {
        private readonly LogEventInfoBuffer _pendingSendBuffer = new LogEventInfoBuffer(10000, false, 10000);
        private bool _sendInProgress;

        /// <summary>
        /// Initializes a new instance of the <see cref="LogReceiverWebServiceTarget"/> class.
        /// </summary>
        public LogReceiverWebServiceTarget()
        {
            Parameters = new List<MethodCallParameter>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LogReceiverWebServiceTarget"/> class.
        /// </summary>
        /// <param name="name">Name of the target.</param>
        public LogReceiverWebServiceTarget(string name) : this()
        {
            Name = name;
        }

        /// <summary>
        /// Gets or sets the endpoint address.
        /// </summary>
        /// <value>The endpoint address.</value>
        /// <docgen category='Connection Options' order='10' />
        public virtual Layout EndpointAddress { get; set; } = Layout.Empty;

        /// <summary>
        /// Gets or sets the name of the endpoint configuration in WCF configuration file.
        /// </summary>
        /// <value>The name of the endpoint configuration.</value>
        /// <docgen category='Connection Options' order='10' />
        public string EndpointConfigurationName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether to use binary message encoding.
        /// </summary>
        /// <docgen category='Payload Options' order='10' />
        public bool UseBinaryEncoding { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use a WCF service contract that is one way (fire and forget) or two way (request-reply)
        /// </summary>
        /// <docgen category='Connection Options' order='10' />
        public bool UseOneWayContract { get; set; }

        /// <summary>
        /// Gets or sets the client ID.
        /// </summary>
        /// <value>The client ID.</value>
        /// <docgen category='Payload Options' order='10' />
        public Layout ClientId { get; set; } = Layout.Empty;

        /// <summary>
        /// Gets the list of parameters.
        /// </summary>
        /// <value>The parameters.</value>
        /// <docgen category='Payload Options' order='10' />
        [ArrayParameter(typeof(MethodCallParameter), "parameter")]
        public IList<MethodCallParameter> Parameters { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether to include per-event properties in the payload sent to the server.
        /// </summary>
        /// <docgen category='Payload Options' order='10' />
        public bool IncludeEventProperties { get; set; }

        /// <inheritdoc/>
        protected override void InitializeTarget()
        {
            if (EndpointAddress is null || ReferenceEquals(EndpointAddress, Layout.Empty))
                throw new NLogConfigurationException("LogReceiverWebServiceTarget EndpointAddress-property must be assigned. EndpointAddress is needed for WCF Client.");
            base.InitializeTarget();
        }

        /// <summary>
        /// Called when log events are being sent (test hook).
        /// </summary>
        /// <param name="events">The events.</param>
        /// <param name="asyncContinuations">The async continuations.</param>
        /// <returns>True if events should be sent, false to stop processing them.</returns>
        protected internal virtual bool OnSend(NLogEvents events, IEnumerable<AsyncLogEventInfo> asyncContinuations)
        {
            return true;
        }

        /// <inheritdoc/>
        protected override void Write(AsyncLogEventInfo logEvent)
        {
            Write((IList<AsyncLogEventInfo>)new[] { logEvent });
        }

        /// <inheritdoc/>
        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            // if web service call is being processed, buffer new events and return
            // lock is being held here
            if (_sendInProgress)
            {
                for (int i = 0; i < logEvents.Count; ++i)
                {
                    PrecalculateVolatileLayouts(logEvents[i].LogEvent);
                    _pendingSendBuffer.Append(logEvents[i]);
                }
                return;
            }

            // Make clone as the input IList will be reused on next call
            AsyncLogEventInfo[] pendingLogEvents = new AsyncLogEventInfo[logEvents.Count];
            logEvents.CopyTo(pendingLogEvents, 0);

            var networkLogEvents = TranslateLogEvents(pendingLogEvents);
            Send(networkLogEvents, pendingLogEvents, null);
        }

        /// <summary>
        /// Flush any pending log messages asynchronously (in case of asynchronous targets).
        /// </summary>
        /// <param name="asyncContinuation">The asynchronous continuation.</param>
        protected override void FlushAsync(AsyncContinuation asyncContinuation)
        {
            SendBufferedEvents(asyncContinuation);
        }

        /// <summary>
        /// Add value to the <see cref="NLogEvents.Strings"/>, returns ordinal in <see cref="NLogEvents.Strings"/>
        /// </summary>
        /// <param name="context"></param>
        /// <param name="stringTable">lookup so only unique items will be added to <see cref="NLogEvents.Strings"/></param>
        /// <param name="value">value to add</param>
        /// <returns></returns>
        private static int AddValueAndGetStringOrdinal(NLogEvents context, Dictionary<string, int> stringTable, string value)
        {
            if (value is null || !stringTable.TryGetValue(value, out var stringIndex))
            {
                stringIndex = context.Strings?.Count ?? 0;
                if (value != null)
                {
                    //don't add null to the string table, that would crash
                    stringTable.Add(value, stringIndex);
                }
                context.Strings?.Add(value ?? string.Empty);
            }

            return stringIndex;
        }

        private NLogEvents TranslateLogEvents(AsyncLogEventInfo[] logEvents)
        {
            if (logEvents.Length == 0 && !LogManager.ThrowExceptions)
            {
                InternalLogger.Error("{0}: LogEvents array is empty, sending empty event...", this);
                return new NLogEvents();
            }

            string clientID = RenderLogEvent(ClientId, logEvents[0].LogEvent) ?? string.Empty;

            var networkLogEvents = new NLogEvents
            {
                ClientName = clientID,
                LayoutNames = new StringCollection(),
                Strings = new StringCollection(),
                BaseTimeUtc = logEvents[0].LogEvent.TimeStamp.ToUniversalTime().Ticks
            };

            var stringTable = new Dictionary<string, int>();

            for (int i = 0; i < Parameters.Count; ++i)
            {
                networkLogEvents.LayoutNames.Add(Parameters[i].Name);
            }

            if (IncludeEventProperties)
            {
                AddEventProperties(logEvents, networkLogEvents);
            }

            networkLogEvents.Events = new NLogEvent[logEvents.Length];
            for (int i = 0; i < logEvents.Length; ++i)
            {
                AsyncLogEventInfo ev = logEvents[i];
                networkLogEvents.Events[i] = TranslateEvent(ev.LogEvent, networkLogEvents, stringTable);
            }

            return networkLogEvents;
        }

        private static void AddEventProperties(AsyncLogEventInfo[] logEvents, NLogEvents networkLogEvents)
        {
            foreach (var ev in logEvents)
            {
                var logEvent = ev.LogEvent;

                if (logEvent.HasProperties)
                {
                    // add all event-level property names in 'LayoutNames' collection.
                    foreach (var prop in logEvent.Properties)
                    {
                        if (prop.Key is string propName && networkLogEvents.LayoutNames?.Contains(propName) == false)
                        {
                            networkLogEvents.LayoutNames.Add(propName);
                        }
                    }
                }
            }
        }

        private void Send(NLogEvents events, AsyncLogEventInfo[] asyncContinuations, AsyncContinuation? flushContinuations)
        {
            if (asyncContinuations.Length == 0)
                return;

            if (!OnSend(events, asyncContinuations))
            {
                flushContinuations?.Invoke(null);
                return;
            }

            var endPointAddress = RenderLogEvent(EndpointAddress, asyncContinuations[0].LogEvent);

            var client = CreateLogReceiver(endPointAddress);
            client.ProcessLogMessagesCompleted += (sender, e) =>
            {
                if (e.Error != null)
                    InternalLogger.Error(e.Error, "{0}: Error while sending", this);

                foreach (var logEvent in asyncContinuations)
                {
                    logEvent.Continuation(e.Error);
                }

                flushContinuations?.Invoke(e.Error);

                // send any buffered events
                SendBufferedEvents(null);
            };

            _sendInProgress = true;
            client.ProcessLogMessagesAsync(events);
        }

        /// <summary>
        /// Creating a new instance of IWcfLogReceiverClient
        ///
        /// Inheritors can override this method and provide their own
        /// service configuration - binding and endpoint address
        /// </summary>
        /// <returns></returns>
        /// <remarks>virtual is used by endusers</remarks>
        protected virtual IWcfLogReceiverClient CreateLogReceiver(string endPointAddress)
        {
            WcfLogReceiverClient client;
            var isSslEndpoint = IsSslEndpoint(endPointAddress);

            if (string.IsNullOrEmpty(EndpointConfigurationName))
            {
                // endpoint not specified - use BasicHttpBinding
                Binding binding;

                if (UseBinaryEncoding)
                {
                    binding = new CustomBinding(new BinaryMessageEncodingBindingElement(), isSslEndpoint ? new HttpsTransportBindingElement() : new HttpTransportBindingElement());
                }
                else
                {
                    binding = CreateBasicHttpBinding(isSslEndpoint);
                }

                client = new WcfLogReceiverClient(UseOneWayContract, binding, new EndpointAddress(endPointAddress));
            }
            else
            {
                client = new WcfLogReceiverClient(UseOneWayContract, EndpointConfigurationName, new EndpointAddress(endPointAddress));
            }

            client.ProcessLogMessagesCompleted += ClientOnProcessLogMessagesCompleted;

            return client;
        }

        private static BasicHttpBinding CreateBasicHttpBinding(bool isSslEndpoint)
        {
            var binding = new BasicHttpBinding();

            binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Windows;
            binding.Security.Mode = isSslEndpoint
                ? BasicHttpSecurityMode.Transport
                : BasicHttpSecurityMode.TransportCredentialOnly;

            return binding;
        }

        private static bool IsSslEndpoint(string url)
        {
            return url.StartsWith("https", StringComparison.InvariantCultureIgnoreCase);
        }

        private void ClientOnProcessLogMessagesCompleted(object sender, AsyncCompletedEventArgs asyncCompletedEventArgs)
        {
            var client = sender as IWcfLogReceiverClient;
            if (client != null && client.State == CommunicationState.Opened)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    client.Abort();
                }
            }
        }

        private void SendBufferedEvents(AsyncContinuation? flushContinuation)
        {
            try
            {
                lock (SyncRoot)
                {
                    // clear inCall flag
                    AsyncLogEventInfo[] pendingLogEvents = _pendingSendBuffer.GetEventsAndClear();
                    if (pendingLogEvents.Length > 0)
                    {
                        var networkLogEvents = TranslateLogEvents(pendingLogEvents);
                        Send(networkLogEvents, pendingLogEvents, flushContinuation);
                    }
                    else
                    {
                        // nothing in the buffer, clear in-call flag
                        _sendInProgress = false;
                        if (flushContinuation != null)
                            flushContinuation(null);
                    }
                }
            }
            catch (Exception exception)
            {
                if (flushContinuation != null)
                {
                    InternalLogger.Error(exception, "{0}: Error in flush async", this);
                    if (LogManager.ThrowExceptions)
                        throw;

                    flushContinuation(exception);
                }
                else
                {
                    // Throwing exceptions here will crash the entire application (.NET 2.0 behavior)
                    InternalLogger.Error(exception, "{0}: Error in send async", this);
                }
            }
        }

        internal NLogEvent TranslateEvent(LogEventInfo eventInfo, NLogEvents context, Dictionary<string, int> stringTable)
        {
            var nlogEvent = new NLogEvent();
#pragma warning disable CS0618 // Type or member is obsolete
            nlogEvent.Id = eventInfo.SequenceID;
#pragma warning restore CS0618 // Type or member is obsolete
            nlogEvent.MessageOrdinal = AddValueAndGetStringOrdinal(context, stringTable, eventInfo.FormattedMessage);
            nlogEvent.LevelOrdinal = eventInfo.Level.Ordinal;
            nlogEvent.LoggerOrdinal = AddValueAndGetStringOrdinal(context, stringTable, eventInfo.LoggerName);
            nlogEvent.TimeDelta = eventInfo.TimeStamp.ToUniversalTime().Ticks - context.BaseTimeUtc;

            for (int i = 0; i < Parameters.Count; ++i)
            {
                var param = Parameters[i];
                var value = param.Layout.Render(eventInfo);
                int stringIndex = AddValueAndGetStringOrdinal(context, stringTable, value);

                nlogEvent.ValueIndexes.Add(stringIndex);
            }

            // layout names beyond Parameters.Count are per-event property names.
            if (context.LayoutNames?.Count > 0)
            {
                for (int i = Parameters.Count; i < context.LayoutNames.Count; ++i)
                {
                    string value;

                    if (eventInfo.HasProperties && eventInfo.Properties.TryGetValue(context.LayoutNames[i], out var propertyValue))
                    {
                        value = Convert.ToString(propertyValue, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        value = string.Empty;
                    }

                    int stringIndex = AddValueAndGetStringOrdinal(context, stringTable, value);
                    nlogEvent.ValueIndexes.Add(stringIndex);
                }
            }

            if (eventInfo.Exception != null)
            {
                var exception = eventInfo.Exception;
#if !NET35
                if (exception is AggregateException aggregateException)
                {
                    if (aggregateException.InnerExceptions?.Count == 1 && !(aggregateException.InnerExceptions[0] is AggregateException))
                        exception = aggregateException.InnerExceptions[0];
                }
#endif
                nlogEvent.ValueIndexes.Add(AddValueAndGetStringOrdinal(context, stringTable, exception.ToString()));
            }

            return nlogEvent;
        }
    }
}