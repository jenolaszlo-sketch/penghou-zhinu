namespace Penghou.Zhinu;

/// <summary>Base for every Zhinu-specific exception. Catch this to handle any Zhinu error without catching general exceptions.</summary>
public class ZhinuException : Exception
{
    public ZhinuException(string message) : base(message) { }

    public ZhinuException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>A referenced run or step does not exist.</summary>
public sealed class WorkflowNotFoundException : WorkflowStateException
{
    public WorkflowNotFoundException(string message) : base(message) { }
}

/// <summary>Raised when a durable execution cannot proceed because of a concurrent owner or fencing race.</summary>
public class WorkflowConcurrencyException : WorkflowStateException
{
    public WorkflowConcurrencyException(string message) : base(message) { }
}

/// <summary>Raised when a durable execution lost its lease or fencing generation to another worker.</summary>
public class WorkflowLeaseException : WorkflowConcurrencyException
{
    public WorkflowLeaseException(string message) : base(message) { }
}

/// <summary>A workflow definition is missing, incompatible, or no longer registered.</summary>
public class WorkflowDefinitionException : ZhinuException
{
    public WorkflowDefinitionException(string message) : base(message) { }
}

/// <summary>An invalid runtime or store configuration was supplied.</summary>
public class WorkflowConfigurationException : ZhinuException
{
    public WorkflowConfigurationException(string message) : base(message) { }

    public WorkflowConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>A caller-facing workflow operation exceeded its configured deadline.</summary>
public sealed class WorkflowTimeoutException : ZhinuException
{
    public WorkflowTimeoutException(string message) : base(message) { }
}

/// <summary>A workflow or activity could not be registered because its identity is already in use.</summary>
public sealed class WorkflowRegistrationException : ZhinuException
{
    public WorkflowRegistrationException(string message) : base(message) { }
}

/// <summary>A transient or corrupt persistence failure escaped the store boundary.</summary>
public class WorkflowPersistenceException : ZhinuException
{
    public WorkflowPersistenceException(string message, Exception innerException) : base(message, innerException) { }
}
