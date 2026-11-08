using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.DTO.TVL;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace AVMTradeReporterTests
{
    /// <summary>Hub context double that records every <see cref="TvlTickDto"/> sent to any TVL group.</summary>
    internal static class TvlTickCapturingHub
    {
        public static IHubContext<BiatecScanHub> Create(List<TvlTickDto> sink)
        {
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((_, args, _) =>
                {
                    // IClientProxy delivers its payload as object?[]; the TVL stream always carries one TvlTickDto.
                    if (args.Length == 1 && args[0] is TvlTickDto tick) lock (sink) sink.Add(tick);
                })
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
            var hub = new Mock<IHubContext<BiatecScanHub>>();
            hub.SetupGet(h => h.Clients).Returns(clients.Object);
            return hub.Object;
        }
    }
}
