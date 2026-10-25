using CliProgram = Clocktail.Cli.Program;
using Clocktail.Cli;

namespace Clocktail.Tests.Cli;

/// <summary>Command-line parsing that used to do something other than what was typed.</summary>
public class CliParsingTests
{
    [Fact]
    public void An_undervolt_with_a_decimal_comma_is_refused_not_read_as_ten_times_as_much()
    {
        Assert.Equal(7_500, VfCommands.Undervolt("7.5"));
        Assert.Equal(25_000, VfCommands.Undervolt("25"));
        Assert.Throws<ArgumentException>(() => VfCommands.Undervolt("7,5"));    // was 75 mV
        Assert.Throws<ArgumentException>(() => VfCommands.Undervolt("1,00"));
        Assert.Throws<ArgumentException>(() => VfCommands.Undervolt("-5"));
        Assert.Throws<ArgumentException>(() => VfCommands.Undervolt("101"));
    }

    [Fact]
    public void A_command_line_with_two_kinds_of_write_names_both()
    {
        Assert.Equal(["power", "P0 clock offsets"], CliProgram.WriteGroups(["--power-limit", "500", "--core-offset", "15"]));
        Assert.Equal(["P0 clock offsets"], CliProgram.WriteGroups(["--core-offset", "15", "--memory-offset", "100"]));   // one kind, two writes
        Assert.Empty(CliProgram.WriteGroups(["--vf", "--gpu", "0"]));                                                   // reads only
        Assert.Equal(2, CliProgram.WriteGroups(["--vf-undervolt", "25", "--xbar-offset", "300"]).Count);
    }
}
