namespace Velo;

/// <summary>An error with its HTTP status (400 / 404 / 409).</summary>
public sealed class VeloException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public static VeloException BadRequest(string message) => new(400, message);
    public static VeloException NotFound(string message) => new(404, message);
    public static VeloException Conflict(string message) => new(409, message);
}
