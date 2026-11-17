using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class UserRequiredLoginInteractionTests
{
    [Fact]
    public async Task UnsetRequiredLoginPreservesOpenRobotAccess()
    {
        var previous = Environment.GetEnvironmentVariable("OPENJIBO_USER_REQUIRED_LOGIN");
        Environment.SetEnvironmentVariable("OPENJIBO_USER_REQUIRED_LOGIN", null);
        try
        {
            var service = CreateService(new InMemoryCloudStateStore(
                Path.Combine(Path.GetTempPath(), $"openjibo-optional-login-{Guid.NewGuid():N}.json")),
                new JiboVerificationService());

            var decision = await service.BuildDecisionAsync(new TurnContext
            {
                DeviceId = "unpaired-device",
                RawTranscript = "hey jibo tell me a joke",
                NormalizedTranscript = "hey jibo tell me a joke"
            });

            Assert.NotEqual("account_required", decision.IntentName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENJIBO_USER_REQUIRED_LOGIN", previous);
        }
    }

    private static JiboInteractionService CreateService(
        ICloudStateStore store, JiboVerificationService verification) =>
        new(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer(),
            new InMemoryPersonalMemoryStore(),
            cloudStateStore: store,
            jiboVerificationService: verification);

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
