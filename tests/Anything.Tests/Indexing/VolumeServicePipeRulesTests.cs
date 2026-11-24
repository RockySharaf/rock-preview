using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// Who may use the volume pipe (<see cref="VolumeService.CreateSecurity"/>). The service, run as SYSTEM, keeps the rules
/// it always had. The Microsoft Store copy's helper, run elevated as the user, needs its administrators group to be able
/// to add the pipe's next instances (one per connection), owns the pipe as administrators (the owner Rock Preview
/// accepts), and refuses SYSTEM that right, so a service starting meanwhile can't add its instances under the helper's
/// rules. The user's own SID never gets to add instances. And the helper serves no pipe someone else made first.
/// </summary>
public sealed class VolumeServicePipeRulesTests
{
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    // A stand-in for the user the pipe is for: a well-known SID, never this PC's account.
    static readonly SecurityIdentifier User = new(WellKnownSidType.BuiltinUsersSid, null);

    const AccessControlType Allow = AccessControlType.Allow, Deny = AccessControlType.Deny;
    // PipeAccessRule adds Synchronize to the rights it is given: left out of both sides of each comparison.
    const PipeAccessRights All = PipeAccessRights.FullControl & ~PipeAccessRights.Synchronize;

    static PipeAccessRule[] Rules(PipeSecurity s) =>
        s.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();

    /// <summary>Each rule as who, allow or deny, and its rights: in the order Windows checks them.</summary>
    static (IdentityReference Who, AccessControlType Type, PipeAccessRights Rights)[] Pinned(PipeSecurity s) =>
        Rules(s).Select(r => (r.IdentityReference, r.AccessControlType, r.PipeAccessRights & ~PipeAccessRights.Synchronize)).ToArray();

    [Fact]
    public void The_service_s_pipe_keeps_the_rules_it_had()
    {
        var s = VolumeService.CreateSecurity([User]);
        (IdentityReference, AccessControlType, PipeAccessRights)[] expected =
        [
            (Network, Deny, All),
            (LocalSystem, Allow, All),
            (Administrators, Allow, PipeAccessRights.ReadWrite),
            (User, Allow, PipeAccessRights.ReadWrite),
        ];
        Assert.Equal(expected, Pinned(s));
        Assert.Null(s.GetOwner(typeof(SecurityIdentifier))); // Windows' default: SYSTEM's, for the service
    }

    [Fact]
    public void The_helper_s_administrators_may_add_instances_and_own_the_pipe_but_SYSTEM_may_not_add_any()
    {
        var s = VolumeService.CreateSecurity([User], asElevatedHelper: true);
        (IdentityReference, AccessControlType, PipeAccessRights)[] expected =
        [
            (Network, Deny, All),
            // SYSTEM's token has the administrators group too: refused outright, whatever that group may.
            (LocalSystem, Deny, PipeAccessRights.CreateNewInstance),
            (LocalSystem, Allow, PipeAccessRights.ReadWrite),
            (Administrators, Allow, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance),
            (User, Allow, PipeAccessRights.ReadWrite),
        ];
        Assert.Equal(expected, Pinned(s));
        Assert.Equal(Administrators, s.GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void A_server_without_the_right_to_add_instances_serves_one_connection_at_a_time()
    {
        // Why the helper's administrators get it: with the service's rules, a program that isn't SYSTEM makes the first
        // instance (making the pipe needs no right on it) but Windows refuses the next one. Here the test's own account,
        // allowed to use the pipe like the user it is for, stands in for the helper.
        using var me = WindowsIdentity.GetCurrent();
        var security = VolumeService.CreateSecurity([me.User!]);
        string name = TestPipeName();
        using var first = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        Assert.Throws<UnauthorizedAccessException>(() => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security).Dispose());
    }

    [Fact]
    public async Task The_helper_serves_no_pipe_someone_else_made_first()
    {
        // Another program (the test, at its own level) takes the name first, with rules that let everyone in and add
        // instances. Every instance gets the rules of the first, so the helper serving it would answer whoever they let
        // in: it ends at once instead, and serves nothing.
        string name = TestPipeName();
        var anyone = new PipeSecurity();
        anyone.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.FullControl, Allow));
        using var taken = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, anyone);
        using var me = WindowsIdentity.GetCurrent();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var helper = Task.Run(() => VolumeService.Run(name, [me.User!], cts.Token, asElevatedHelper: true));
        Assert.True(await Task.WhenAny(helper, Task.Delay(TimeSpan.FromSeconds(10))) == helper, "the helper went on serving a pipe someone else made");
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public void A_first_instance_is_refused_where_the_pipe_is_there_already()
    {
        // What the helper's first instance relies on (PipeOptions.FirstPipeInstance): Windows refuses it when anyone's
        // pipe has the name, even one whose rules would let it add an instance.
        using var me = WindowsIdentity.GetCurrent();
        var open = new PipeSecurity();
        open.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, Allow));
        string name = TestPipeName();
        using var taken = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, open);
        using (NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, open)) { } // an instance of it: allowed
        Assert.Throws<UnauthorizedAccessException>(() => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0, 0, open).Dispose());
    }

    [Fact]
    public void A_pipe_s_owner_is_told()
    {
        // The owner every instance of a pipe reports: the account that made it (administrators when it runs elevated),
        // which the helper checks after each instance it makes.
        using var me = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator) ? Administrators : me.User!;
        using var pipe = NamedPipeServerStreamAcl.Create(TestPipeName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, VolumeService.CreateSecurity([me.User!]));
        Assert.True(VolumeService.IsOwnedBy(pipe, expected));
        Assert.False(VolumeService.IsOwnedBy(pipe, LocalSystem));
    }

    /// <summary>A pipe name of the test's own, never the real one (<see cref="VolumeService.PipeName"/>).</summary>
    static string TestPipeName() => "RockPreview.Tests." + Guid.NewGuid().ToString("N");
}
