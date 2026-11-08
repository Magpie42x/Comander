namespace Comander.Services;

/// <summary>
/// Сквозной прогресс переноса файлов (запрос пользователя): позиция по списку,
/// байты текущего объекта, отмена. Живёт в фоновой задаче копирования; UI только
/// подписывается — работать в это время можно.
/// </summary>
public sealed class TransferProgress
{
    readonly CancellationTokenSource _cts = new();
    DateTime _lastFire = DateTime.MinValue;

    public int FileCount { get; }
    public int FileIndex { get; private set; }          // 1-based
    public string CurrentName { get; private set; } = "";
    public long BytesDone { get; private set; }
    public long BytesTotal { get; private set; }        // размер текущего объекта; 0 — неизвестен (папка)
    public bool Cancelled => _cts.IsCancellationRequested;
    public CancellationToken Token => _cts.Token;

    /// <summary>Изменение состояния (троттлинг ~120 мс, плюс всегда при BeginFile/Finish).</summary>
    public event Action? Changed;
    /// <summary>Перенос завершён (summary — итог для статусбара).</summary>
    public event Action<string>? Finished;

    public TransferProgress(int fileCount) => FileCount = Math.Max(1, fileCount);

    public void Cancel() => _cts.Cancel();

    public void BeginFile(int index, string name, long total)
    {
        FileIndex = index;
        CurrentName = name;
        BytesDone = 0;
        BytesTotal = total;
        Fire(force: true);
    }

    /// <summary>Добавить перекачанные байты (дельта относительно предыдущего вызова).</summary>
    public void AddBytes(long delta)
    {
        if (delta <= 0) return;
        BytesDone += delta;
        Fire(force: false);
    }

    public void Finish(string summary) => Finished?.Invoke(summary);

    void Fire(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastFire < TimeSpan.FromMilliseconds(120)) return;
        _lastFire = now;
        Changed?.Invoke();
    }
}
