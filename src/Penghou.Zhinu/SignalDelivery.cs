namespace Penghou.Zhinu;

/// <summary>
/// The result of delivering a buffered external signal to a waiting step.
/// A non-null value means a signal was consumed; the payload may be null when
/// the sender supplied no data.
/// </summary>
public readonly record struct SignalDelivery(string? DataJson);
