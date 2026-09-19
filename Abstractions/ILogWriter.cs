namespace Velo.Abstractions;

public interface ILogWriter
{
    void Write(string category, string message);
}