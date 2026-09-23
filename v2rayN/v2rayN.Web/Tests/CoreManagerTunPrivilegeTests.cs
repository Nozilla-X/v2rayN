using ServiceLib.Enums;
using ServiceLib.Manager;

namespace v2rayN.Web.Tests;

public class CoreManagerTunPrivilegeTests
{
    [Test]
    public async Task LinuxOrdinaryUserWithoutNetAdminStillUsesSudo()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: false).Should().BeTrue();
    }

    [Test]
    public async Task LinuxRootDoesNotUseSudo()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: true,
            hasEffectiveNetAdmin: false).Should().BeFalse();
    }

    [Test]
    public async Task LinuxNetAdminCapabilityDoesNotUseSudo()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.sing_box,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: true).Should().BeFalse();
    }

    [Test]
    public async Task WindowsAndNonTunLaunchKeepExistingPolicy()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: false,
            isLinux: false,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: false).Should().BeFalse();
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: false,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: true,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: true).Should().BeFalse();
    }

    [Test]
    public async Task OtherNonWindowsPlatformsKeepExistingSudoPolicy()
    {
        await CoreManager.ShouldRunAsSudoAfterLinuxPrivilegeCheck(
            isTunLaunch: true,
            ECoreType.Xray,
            isNonWindows: true,
            isLinux: false,
            effectiveUserIsRoot: false,
            hasEffectiveNetAdmin: false).Should().BeTrue();
    }
}
