using ClaudeTrayApp.Startup;
using Shouldly;

namespace ClaudeTrayApp.Tests;

/// <summary>
/// A named Mutex is a real OS object, so <see cref="SingleInstance"/> is fully testable in-process: a second
/// <see cref="SingleInstance.TryAcquire(string, string, out SingleInstance?)"/> call finding the mutex already held
/// is the same code path a genuine second process would hit. Every test uses a GUID-suffixed name rather than the
/// production "Local\ClaudeUsageTray.*" names: this machine may have a real instance already running, and colliding
/// with its mutex would make the test fail for reasons unrelated to this code, and the activation signal would pop
/// that real instance's flyout as a side effect of running the test suite.
/// </summary>
public class SingleInstanceTests
{
    private static (string Mutex, string Activation) UniqueNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return (@"Local\ClaudeUsageTray.Test." + id, @"Local\ClaudeUsageTray.Test." + id + ".Activate");
    }

    [Fact]
    public void First_acquire_succeeds_and_a_second_is_refused()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        try
        {
            SingleInstance.TryAcquire(mutexName, eventName, out var second).ShouldBeFalse();
            second.ShouldBeNull();
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void A_second_acquire_signals_the_first_instances_activation()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        try
        {
            using var activated = new ManualResetEventSlim(false);
            first!.ListenForActivation(() => activated.Set());

            SingleInstance.TryAcquire(mutexName, eventName, out var second).ShouldBeFalse();
            second.ShouldBeNull();

            activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void A_signal_sent_before_the_first_instance_listens_is_not_lost()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        try
        {
            SingleInstance.TryAcquire(mutexName, eventName, out _).ShouldBeFalse();

            using var activated = new ManualResetEventSlim(false);
            first!.ListenForActivation(() => activated.Set());

            activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void Acquire_succeeds_again_after_the_holder_is_disposed()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();
        first!.Dispose();

        SingleInstance.TryAcquire(mutexName, eventName, out var second).ShouldBeTrue();
        second.ShouldNotBeNull();
        second!.Dispose();
    }

    [Fact]
    public void The_default_account_keeps_the_original_names_and_others_append_their_key()
    {
        SingleInstance.MutexNameFor(null).ShouldBe(@"Local\ClaudeUsageTray.Instance");
        SingleInstance.ActivationEventNameFor(null).ShouldBe(@"Local\ClaudeUsageTray.ShowFlyout");
        SingleInstance.MutexNameFor("claude-work-3f9a1c07").ShouldBe(@"Local\ClaudeUsageTray.Instance.claude-work-3f9a1c07");
        SingleInstance.ActivationEventNameFor("claude-work-3f9a1c07").ShouldBe(@"Local\ClaudeUsageTray.ShowFlyout.claude-work-3f9a1c07");
    }

    [Fact]
    public void Two_accounts_run_side_by_side_and_each_relaunch_signals_only_its_own()
    {
        var (prefix, _) = UniqueNames();
        var personal = (Mutex: prefix + ".personal", Activation: prefix + ".personal.Activate");
        var work = (Mutex: prefix + ".work", Activation: prefix + ".work.Activate");

        SingleInstance.TryAcquire(personal.Mutex, personal.Activation, out var first).ShouldBeTrue();
        SingleInstance.TryAcquire(work.Mutex, work.Activation, out var second).ShouldBeTrue();
        try
        {
            using var personalActivated = new ManualResetEventSlim(false);
            using var workActivated = new ManualResetEventSlim(false);
            first!.ListenForActivation(personalActivated.Set);
            second!.ListenForActivation(workActivated.Set);

            SingleInstance.TryAcquire(work.Mutex, work.Activation, out _).ShouldBeFalse();

            workActivated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
            personalActivated.Wait(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken).ShouldBeFalse();
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Fact]
    public void IsRunning_reports_a_held_mutex_without_taking_it()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.IsRunning(mutexName).ShouldBeFalse();
        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        try
        {
            SingleInstance.IsRunning(mutexName).ShouldBeTrue();
            SingleInstance.IsRunning(mutexName).ShouldBeTrue();
        }
        finally
        {
            first!.Dispose();
        }

        SingleInstance.IsRunning(mutexName).ShouldBeFalse();
    }
}
