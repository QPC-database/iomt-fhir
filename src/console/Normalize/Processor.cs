﻿using EnsureThat;
using Microsoft.Azure.EventHubs;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Options;
using Microsoft.Health.Events.EventConsumers;
using Microsoft.Health.Events.Model;
using Microsoft.Health.Fhir.Ingest.Console.Template;
using Microsoft.Health.Fhir.Ingest.Data;
using Microsoft.Health.Fhir.Ingest.Service;
using Microsoft.Health.Fhir.Ingest.Telemetry;
using Microsoft.Health.Fhir.Ingest.Template;
using Microsoft.Health.Logging.Telemetry;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static Microsoft.Azure.EventHubs.EventData;

namespace Microsoft.Health.Fhir.Ingest.Console.Normalize
{
    public class Processor : IEventConsumer
    {
        private string _templateDefinition;
        private ITemplateManager _templateManager;
        private ITelemetryLogger _logger;
        private IAsyncCollector<IMeasurement> _collector;
        private IOptions<NormalizationServiceOptions> _normalizationOptions;
        private IEventProcessingMeter _eventProcessingMeter = new EventProcessingMeter();

        public Processor(
            [Blob("template/%Template:DeviceContent%", FileAccess.Read)] string templateDefinition,
            ITemplateManager templateManager,
            IAsyncCollector<IMeasurement> collector,
            ITelemetryLogger logger,
            IOptions<NormalizationServiceOptions> options)
        {
            _templateDefinition = templateDefinition;
            _templateManager = templateManager;
            _collector = collector;
            _logger = logger;
            _normalizationOptions = options;
        }

        public async Task ConsumeAsync(IEnumerable<IEventMessage> events)
        {
            EnsureArg.IsNotNull(_templateDefinition);
            var templateContent = _templateManager.GetTemplateAsString(_templateDefinition);

            var templateContext = CollectionContentTemplateFactory.Default.Create(templateContent);
            templateContext.EnsureValid();
            var template = templateContext.Template;

            _logger.LogMetric(
                IomtMetrics.DeviceEvent(),
                    events.Count());

            IEnumerable<EventData> eventHubEvents = events
                .Select(x =>
                {
                    var eventData = new EventData(x.Body.ToArray());

                    eventData.SystemProperties = new SystemPropertiesCollection(
                        x.SequenceNumber,
                        x.EnqueuedTime.UtcDateTime,
                        x.Offset.ToString(),
                        x.PartitionId);

                    foreach (KeyValuePair<string, object> entry in x.Properties)
                    {
                        eventData.Properties[entry.Key] = entry.Value;
                    }

                    foreach (KeyValuePair<string, object> entry in x.SystemProperties)
                    {
                        eventData.SystemProperties.TryAdd(entry.Key, entry.Value);
                    }

                    foreach (KeyValuePair<string, object> entry in x.Properties)
                    {
                        eventData.Properties[entry.Key] = entry.Value;
                    }

                    return eventData;
                });

            var dataNormalizationService = new MeasurementEventNormalizationService(_logger, template);
            await dataNormalizationService.ProcessAsync(eventHubEvents, _collector).ConfigureAwait(false);

            if (_normalizationOptions.Value.LogDeviceIngressSizeBytes)
            {
                var eventStats = await _eventProcessingMeter.CalculateEventStats(eventHubEvents);

                _logger.LogMetric(
                    IomtMetrics.DeviceIngressSizeBytes(),
                    eventStats.TotalEventsProcessedBytes);
            }
        }
    }
}
