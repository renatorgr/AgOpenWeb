// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.ViewModels.Wizards;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A step's live readings (the roll, the WAS, the wheel angle) are set from the module's data
/// thread, while the host loop enters the step again (which clears what it changed) and asks
/// what it changed. The sets behind that were plain HashSets: changed from both threads at once
/// they threw "Operations that change non-concurrent collections must have exclusive access",
/// and the steer wizard harness stopped in the Roll step (mini PC night, 25 Nov 2026).
/// </summary>
[TestFixture]
public class WizardStepTouchedThreadTests
{
    private sealed class LiveStep : WizardStepViewModel
    {
        public override string Title => "Live";
        public override string Description => "";

        private double _live;
        public double Live
        {
            get => _live;
            set => SetProperty(ref _live, value);
        }

        /// <summary>A property changed under another name, as a step with many live fields raises them.</summary>
        public void Raise(string name) => OnPropertyChanged(name);

        public bool LiveTouched => Touched(nameof(Live)) | TouchedInWizard(nameof(Live));
    }

    [Test]
    public void Readings_from_the_module_thread_while_the_host_loop_enters_and_asks_do_not_throw()
    {
        var step = new LiveStep();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? failure = null;

        // Many names, so the set grows and is rehashed while the other thread reads or clears it.
        var module = Task.Run(() =>
        {
            int i = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    step.Live = i++;
                    step.Raise("Reading" + (i % 512));
                }
            }
            catch (Exception ex) { failure ??= ex; stop.Cancel(); }
        });

        try
        {
            while (!stop.IsCancellationRequested)
            {
                step.IsActive = true;
                _ = step.LiveTouched;
                step.IsActive = false;
            }
        }
        catch (Exception ex) { failure ??= ex; stop.Cancel(); }

        module.Wait();
        Assert.That(failure, Is.Null, failure?.ToString());
    }
}
