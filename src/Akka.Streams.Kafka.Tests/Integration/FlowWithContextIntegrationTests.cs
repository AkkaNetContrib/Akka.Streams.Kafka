using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Streams.Dsl;
using Akka.Streams.Implementation.Fusing;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Extensions;
using Akka.Streams.Kafka.Helpers;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Confluent.Kafka;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Kafka.Tests.Integration
{
    public class FlowWithContextIntegrationTests : KafkaIntegrationTests
    {
        public FlowWithContextIntegrationTests(ITestOutputHelper output, KafkaFixture fixture) 
            : base(nameof(FlowWithContextIntegrationTests), output, fixture)
        {
        }

        [Fact]
        public async Task ProducerFlowWithContext_should_work_with_source_with_context()
        {
            bool Duplicate(string value) => value == "1";
            bool Ignore(string value) => value == "2";

            var consumerSettings = CreateConsumerSettings<string, string>(CreateGroup(1));
            var topic1 = CreateTopic(1);
            var topic2 = CreateTopic(2);
            var topic3 = CreateTopic(3);
            var topic4 = CreateTopic(4);
            var producerSettings = BuildProducerSettings<string, string>();
            var committerSettings = CommitterSettings;
            var totalMessages = 10;
            
            await ProduceStrings(topic1, Enumerable.Range(1, totalMessages), producerSettings);

            var control = KafkaConsumer.SourceWithOffsetContext(consumerSettings, Subscriptions.Topics(topic1))
                .Select(record =>
                {
                    IEnvelope<string, string, NotUsed> output;
                    if (Duplicate(record.Value))
                    {
                        output = ProducerMessage.Multi(new[]
                        {
                            new ProducerRecord<string, string>(topic2, record.Key, record.Value),
                            new ProducerRecord<string, string>(topic3, record.Key, record.Value)
                        }.ToImmutableSet());
                    }
                    else if (Ignore(record.Value))
                    {
                        output = ProducerMessage.PassThrough<string, string>();
                    }
                    else
                    {
                        output = ProducerMessage.Single(new ProducerRecord<string, string>(topic4, record.Key, record.Value));
                    }

                    Log.Debug($"Giving message of type {output.GetType().Name}");
                    return output;
                })
                .Via(KafkaProducer.FlowWithContext<string, string, ICommittableOffset>(producerSettings))
                .AsSource()
                .Log("Produced messages", r => $"Committing {r.Item2.Offset.Topic}:{r.Item2.Offset.Partition}[{r.Item2.Offset.Offset}]")
                .ToMaterialized(Committer.SinkWithOffsetContext<IResults<string, string, ICommittableOffset>>(committerSettings), Keep.Both)
                .MapMaterializedValue(tuple => DrainingControl<NotUsed>.Create(tuple.Item1, tuple.Item2.ContinueWith(t => NotUsed.Instance)))
                .Run(Materializer);
            
            var (control2, result) = KafkaConsumer.PlainSource(consumerSettings, Subscriptions.Topics(topic2, topic3, topic4))
                .ToMaterialized(Sink.Seq<ConsumeResult<string, string>>(), Keep.Both)
                .Run(Materializer);

            // Give it some time to handle message flow
            await Task.Delay(TimeSpan.FromSeconds(10));

            var shutdown1 = control.DrainAndShutdown();
            AwaitCondition(() => shutdown1.IsCompletedSuccessfully, TimeSpan.FromSeconds(20));
            var shutdown2 = control2.Shutdown();
            AwaitCondition(() => shutdown2.IsCompletedSuccessfully, TimeSpan.FromSeconds(10));
            
            AwaitCondition(() => result.IsCompletedSuccessfully, TimeSpan.FromSeconds(10));
            result.Result.Should().HaveCount(totalMessages);
        }
    }
}