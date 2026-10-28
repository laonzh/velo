using System.Text.Json;

namespace Velo.Tests;

public class ParseIdTests
{
    [Theory]
    [InlineData("42", 42)]
    [InlineData("VL-42", 42)]
    [InlineData("vl-42", 42)]
    [InlineData("Vl-7", 7)]
    public void Accepts(string id, int expected) => Assert.Equal(expected, Engine.ParseId(id));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("abc")]
    [InlineData("vl-")]
    [InlineData("4x")]
    [InlineData("vl-vl-4")]
    [InlineData("99999999999")]
    public void Rejects(string id) => Assert.Equal(400, Assert.Throws<VeloException>(() => Engine.ParseId(id)).Status);
}
