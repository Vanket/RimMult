using RimMult.Shared.Time;

namespace RimMult.Tests;

public class TimeCoordinatorTests
{
    private static TimeCoordinator Create(SpeedVoteMode mode = SpeedVoteMode.Lowest, bool anyoneCanPause = true) =>
        new(new TimeSettings { VoteMode = mode, AnyoneCanPause = anyoneCanPause, MaxSpeed = GameSpeed.Ultrafast, MaxDriftTicks = 60, MaxDriftSeconds = 0 });

    [Fact]
    public void NoVotesMeansPaused()
    {
        Assert.Equal(GameSpeed.Paused, Create().ResolveSpeed());
    }

    [Fact]
    public void LowestVoteWins()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Superfast);
        time.SetVote(2, GameSpeed.Fast);
        Assert.Equal(GameSpeed.Fast, time.ResolveSpeed());
    }

    [Fact]
    public void AbstainingPlayersAreIgnored()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Superfast);
        time.SetVote(2, GameSpeed.Normal);
        time.SetVote(2, null);
        Assert.Equal(GameSpeed.Superfast, time.ResolveSpeed());
    }

    [Fact]
    public void AnyPauseVotePausesWhenAllowed()
    {
        var time = Create(SpeedVoteMode.Majority);
        time.SetVote(1, GameSpeed.Fast);
        time.SetVote(2, GameSpeed.Fast);
        time.SetVote(3, GameSpeed.Paused);
        Assert.Equal(GameSpeed.Paused, time.ResolveSpeed());
    }

    [Fact]
    public void MajorityIgnoresSinglePauseWhenPausingIsNotFree()
    {
        var time = Create(SpeedVoteMode.Majority, anyoneCanPause: false);
        time.SetVote(1, GameSpeed.Fast);
        time.SetVote(2, GameSpeed.Fast);
        time.SetVote(3, GameSpeed.Paused);
        Assert.Equal(GameSpeed.Fast, time.ResolveSpeed());
    }

    [Fact]
    public void MajorityTieGoesToSlowerSpeed()
    {
        var time = Create(SpeedVoteMode.Majority);
        time.SetVote(1, GameSpeed.Superfast);
        time.SetVote(2, GameSpeed.Normal);
        Assert.Equal(GameSpeed.Normal, time.ResolveSpeed());
    }

    [Fact]
    public void HostModeUsesOnlyHostVote()
    {
        var time = Create(SpeedVoteMode.Host);
        time.HostPlayerId = 2;
        time.SetVote(1, GameSpeed.Normal);
        time.SetVote(2, GameSpeed.Superfast);
        Assert.Equal(GameSpeed.Superfast, time.ResolveSpeed());
    }

    [Fact]
    public void SpeedIsCappedByMaxSpeed()
    {
        var time = new TimeCoordinator(new TimeSettings { MaxSpeed = GameSpeed.Fast });
        time.SetVote(1, GameSpeed.Ultrafast);
        Assert.Equal(GameSpeed.Fast, time.ResolveSpeed());
    }

    [Fact]
    public void HorizonKeepsEveryoneWithinDriftOfTheSlowest()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Fast);
        time.ReportAuthority(1, tick: 1000, sustainableTicksPerSecond: 1000);
        time.ReportAuthority(2, tick: 900, sustainableTicksPerSecond: 1000);

        var grant = time.ComputeGrant();
        Assert.Equal(960, grant.HorizonTick);
        Assert.Equal(GameSpeed.Fast, grant.Speed);
    }

    [Fact]
    public void HorizonNeverMovesBackwards()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Fast);
        time.ReportAuthority(1, 1000, 1000);
        Assert.Equal(1060, time.ComputeGrant().HorizonTick);

        // A new, behind-schedule authority joins: the horizon holds instead of rewinding.
        time.ReportAuthority(2, 500, 1000);
        Assert.Equal(1060, time.ComputeGrant().HorizonTick);
    }

    [Fact]
    public void WhilePausedLaggardsMayCatchUpToTheLeader()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Paused);
        time.ReportAuthority(1, 1000, 1000);
        time.ReportAuthority(2, 970, 1000);
        Assert.Equal(1000, time.ComputeGrant().HorizonTick);
    }

    [Fact]
    public void SlowMachineIsReportedAsBottleneck()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Superfast); // 360 tps
        time.ReportAuthority(1, 0, 900);
        time.ReportAuthority(2, 0, 200);
        time.ReportAuthority(3, 0, 300);

        Assert.Equal(2, time.ComputeGrant().BottleneckPlayerId);
    }

    [Fact]
    public void SmallShortfallIsNotABottleneck()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Normal); // 60 tps
        time.ReportAuthority(1, 0, 58);
        Assert.Equal(-1, time.ComputeGrant().BottleneckPlayerId);
    }

    [Fact]
    public void RemovedPlayerNoLongerHoldsBackTheWorld()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Fast);
        time.SetVote(2, GameSpeed.Normal);
        time.ReportAuthority(1, 1000, 1000);
        time.ReportAuthority(2, 100, 50);

        time.RemovePlayer(2);
        var grant = time.ComputeGrant();
        Assert.Equal(GameSpeed.Fast, grant.Speed);
        Assert.Equal(1060, grant.HorizonTick);
        Assert.Equal(-1, grant.BottleneckPlayerId);
    }

    [Fact]
    public void DriftScalesWithSpeed()
    {
        var time = new TimeCoordinator(new TimeSettings { MaxDriftTicks = 60, MaxDriftSeconds = 1.5f, MaxSpeed = GameSpeed.Superfast });
        Assert.Equal(60, time.DriftTicks(GameSpeed.Paused));
        Assert.Equal(90, time.DriftTicks(GameSpeed.Normal));     // 60 tps * 1.5 s
        Assert.Equal(540, time.DriftTicks(GameSpeed.Superfast)); // 360 tps * 1.5 s

        time.SetVote(1, GameSpeed.Superfast);
        time.ReportAuthority(1, 1000, 1000);
        time.ReportAuthority(2, 1000, 1000);
        Assert.Equal(1540, time.ComputeGrant().HorizonTick);
    }

    [Fact]
    public void AnyoneCanUnpause()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Fast);
        time.SetVote(2, GameSpeed.Paused);
        Assert.Equal(GameSpeed.Paused, time.ResolveSpeed());

        time.SetVote(1, GameSpeed.Superfast);
        Assert.Equal(GameSpeed.Superfast, time.ResolveSpeed());

        // The player who had paused can still vote again afterwards.
        time.SetVote(2, GameSpeed.Normal);
        Assert.Equal(GameSpeed.Normal, time.ResolveSpeed());
    }

    [Fact]
    public void MajorityPauseIsNotLiftedBySingleVote()
    {
        var time = Create(SpeedVoteMode.Majority, anyoneCanPause: false);
        time.SetVote(1, GameSpeed.Paused);
        time.SetVote(2, GameSpeed.Paused);
        time.SetVote(3, GameSpeed.Fast);
        Assert.Equal(GameSpeed.Paused, time.ResolveSpeed());
    }

    [Fact]
    public void HoldPausesUntilReleasedOrPlayerGone()
    {
        var time = Create();
        time.SetVote(1, GameSpeed.Fast);
        time.SetHold(2, true);
        Assert.True(time.AnyHold);
        Assert.Equal(GameSpeed.Paused, time.ResolveSpeed());

        // Voting a speed doesn't lift a hold the way it lifts a pause vote.
        time.SetVote(1, GameSpeed.Superfast);
        Assert.Equal(GameSpeed.Paused, time.ResolveSpeed());

        time.SetHold(2, false);
        Assert.Equal(GameSpeed.Superfast, time.ResolveSpeed());

        time.SetHold(3, true);
        time.RemovePlayer(3);
        Assert.False(time.AnyHold);
        Assert.Equal(GameSpeed.Superfast, time.ResolveSpeed());
    }
}
