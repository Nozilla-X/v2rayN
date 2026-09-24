namespace ServiceLib.Tests.Manager;

public class CoreManagerTests
{
    [Test]
    [Arguments(ECoreType.sing_box)]
    [Arguments(ECoreType.mihomo)]
    [Arguments(ECoreType.Xray)]
    public async Task ShouldRunAsSudo_TunLaunchOnNonWindows_RequiresElevation(ECoreType coreType)
    {
        await CoreManager.ShouldRunAsSudo(isTunLaunch: true, coreType, isNonWindows: true).Should().BeTrue();
    }

    [Test]
    public async Task ShouldRunAsSudo_NonTunLaunch_ShouldNotElevate()
    {
        // Regression guard for the macOS TUN failure: the elevation decision must follow
        // the context snapshot that generated the config. A launch whose snapshot has TUN
        // disabled must never elevate, and a launch whose snapshot has TUN enabled must
        // elevate regardless of later changes to the live config.
        await CoreManager.ShouldRunAsSudo(isTunLaunch: false, ECoreType.sing_box, isNonWindows: true).Should().BeFalse();
        await CoreManager.ShouldRunAsSudo(isTunLaunch: false, ECoreType.Xray, isNonWindows: true).Should().BeFalse();
    }

    [Test]
    public async Task ShouldRunAsSudo_OnWindows_ShouldNotElevate()
    {
        await CoreManager.ShouldRunAsSudo(isTunLaunch: true, ECoreType.sing_box, isNonWindows: false).Should().BeFalse();
    }

    [Test]
    public async Task LinuxRootWithEffectiveNetAdminRunsTunCoreDirectly()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: true,
            hasEffectiveNetAdmin: true,
            hasAmbientNetAdmin: false).Should().BeFalse();
    }

    [Test]
    public async Task LinuxRootWithoutEffectiveNetAdminFallsBackToSudo()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: true,
            hasEffectiveNetAdmin: false,
            hasAmbientNetAdmin: true).Should().BeTrue();
        await CoreManager.CanRunTunCoreWithoutSudo(
            isLinux: true,
            effectiveUserIsRoot: true,
            hasEffectiveNetAdmin: false,
            hasAmbientNetAdmin: true).Should().BeFalse();
    }

    [Test]
    public async Task LinuxAmbientNetAdminRunsTunCoreDirectlyAfterExec()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.sing_box,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: false,
            hasAmbientNetAdmin: true).Should().BeFalse();
        await CoreManager.CanRunTunCoreWithoutSudo(
                isLinux: true,
                effectiveUserIsRoot: false,
                hasEffectiveNetAdmin: false,
                hasAmbientNetAdmin: true)
            .Should().BeTrue();
    }

    [Test]
    public async Task NonRootWithOnlyEffectiveNetAdminFallsBackToSudo()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: true,
            hasAmbientNetAdmin: false).Should().BeTrue();
        await CoreManager.CanRunTunCoreWithoutSudo(
                isLinux: true,
                effectiveUserIsRoot: false,
                hasEffectiveNetAdmin: true,
                hasAmbientNetAdmin: false)
            .Should().BeFalse();
    }

    [Test]
    public async Task CapabilityDetectionReadsTheRequestedStatusFieldAndFailsClosed()
    {
        await CoreManager.HasEffectiveNetAdminCapability("CapEff:\t0000000000001000").Should().BeTrue();
        await CoreManager.HasEffectiveNetAdminCapability("CapEff:\t0000000000000000").Should().BeFalse();
        await CoreManager.HasEffectiveNetAdminCapability("CapAmb:\t0000000000001000").Should().BeFalse();
        await CoreManager.HasEffectiveNetAdminCapability(null).Should().BeFalse();
        await CoreManager.HasAmbientNetAdminCapability("CapAmb:\t0000000000001000").Should().BeTrue();
        await CoreManager.HasAmbientNetAdminCapability("CapAmb:\t0000000000000000").Should().BeFalse();
        await CoreManager.HasAmbientNetAdminCapability("CapEff:\t0000000000001000").Should().BeFalse();
        await CoreManager.HasAmbientNetAdminCapability(null).Should().BeFalse();
    }

    [Test]
    public async Task WindowsAndOtherNonLinuxPoliciesRemainUnchanged()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: false,
            isLinux: false,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: false,
            hasAmbientNetAdmin: false).Should().BeFalse();
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: false,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: false,
            hasAmbientNetAdmin: false).Should().BeTrue();
    }

    [Test]
    [Arguments(ECoreType.v2fly)]
    [Arguments(ECoreType.hysteria)]
    [Arguments(null)]
    public async Task ShouldRunAsSudo_UnsupportedCoreType_ShouldNotElevate(ECoreType? coreType)
    {
        await CoreManager.ShouldRunAsSudo(isTunLaunch: true, coreType, isNonWindows: true).Should().BeFalse();
    }
}
