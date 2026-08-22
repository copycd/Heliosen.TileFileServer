namespace Heliosen.TileServer.Layers;

/// <summary>
/// 카탈로그의 심장 박동. 1 초마다 <see cref="LayerCatalog.Tick"/> 을 불러서
/// 디바운스된 훑기와 주기적 훑기를 진행시킨다.
///
/// 여기서 예외가 밖으로 나가면 호스트가 내려간다. 그래서 전부 삼킨다.
/// </summary>
internal sealed class LayerCatalogWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly LayerCatalog _catalog;
    private readonly ILogger<LayerCatalogWorker> _log;

    public LayerCatalogWorker(LayerCatalog catalog, ILogger<LayerCatalogWorker> log)
    {
        _catalog = catalog;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                _catalog.Tick();
            }
            catch (Exception ex)
            {
                // 여기까지 예외가 올라오면 안 되지만, 와도 루프는 계속 돌아야 한다.
                _log.LogError(ex, "레이어 감시 주기 처리 중 오류.");
            }
        }
    }
}
