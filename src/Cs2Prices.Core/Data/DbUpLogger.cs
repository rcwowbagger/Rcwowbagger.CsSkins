using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace Cs2Prices.Core.Data;

/// <summary>Routes DbUp output into the application's logger (Serilog in the hosts).</summary>
internal sealed class DbUpLogger(ILogger logger) : IUpgradeLog
{
    public void LogTrace(string format, params object[] args) => logger.LogTrace("DbUp: " + format, args);

    public void LogDebug(string format, params object[] args) => logger.LogDebug("DbUp: " + format, args);

    public void LogInformation(string format, params object[] args) => logger.LogInformation("DbUp: " + format, args);

    public void LogWarning(string format, params object[] args) => logger.LogWarning("DbUp: " + format, args);

    public void LogError(string format, params object[] args) => logger.LogError("DbUp: " + format, args);

    public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, "DbUp: " + format, args);
}
