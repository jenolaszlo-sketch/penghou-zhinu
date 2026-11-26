namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// The only operations eligible for mapped-section recovery. Operation identity
/// is explicit; it is never inferred from SQL text or exception messages.
/// </summary>
internal enum SqliteRecoveryOperation
{
    None = 0,
    SchemaVerify = 1,
    LeaseRecoverBegin = 2
}
