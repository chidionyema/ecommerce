using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using ecommerce.Contracts;

namespace ecommerce.Contracts
{
    public interface ITelemetryService
    {
        /// <summary>
        /// Track an event with a name.
        /// </summary>
        void TrackEvent(string eventName);

        /// <summary>
        /// Track an event with a name and a dictionary of properties (object values).
        /// </summary>
        void TrackEvent(string eventName, IDictionary<string, object> properties);

        /// <summary>
        /// Track an event with a name and a dictionary of properties (string values).
        /// </summary>
        void TrackEvent(string eventName, IDictionary<string, string> properties);

        /// <summary>
        /// Track an exception.
        /// </summary>
        void TrackException(Exception ex);
    }
}

namespace ecommerce.Services
{
    public class ConsoleTelemetryService : ITelemetryService
    {
        private readonly ILogger<ConsoleTelemetryService> _logger;

        public ConsoleTelemetryService(ILogger<ConsoleTelemetryService> logger)
        {
            _logger = logger;
        }

        public void TrackEvent(string eventName)
        {
            _logger.LogInformation("Telemetry Event: {EventName}", eventName);
        }

        public void TrackEvent(string eventName, IDictionary<string, object> properties)
        {
            _logger.LogInformation("Telemetry Event: {EventName} with properties: {@Properties}", eventName, properties);
        }

        public void TrackEvent(string eventName, IDictionary<string, string> properties)
        {
            _logger.LogInformation("Telemetry Event: {EventName} with properties: {@Properties}", eventName, properties);
        }

        public void TrackException(Exception ex)
        {
            _logger.LogError(ex, "Telemetry Exception: {ErrorMessage}", ex.Message);
        }
    }
}
