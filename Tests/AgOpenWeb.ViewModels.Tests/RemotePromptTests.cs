// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#109: the host's confirms, errors and failures reach the web client.</summary>
[TestFixture]
public class RemotePromptTests
{
    [Test]
    public void Confirm_IsPendingUntilAnswered_AndYesRunsTheAction()
    {
        var vm = new MainViewModelBuilder().Build();
        bool ran = false;

        vm.ShowConfirmationDialog("Title", "Message", () => ran = true);

        Assert.That(vm.IsPromptPending, Is.True);
        Assert.That(vm.AnswerPrompt(vm.PromptSeq, confirm: true, checkboxChecked: false), Is.True);
        Assert.That(ran, Is.True);
        Assert.That(vm.IsPromptPending, Is.False);
    }

    [Test]
    public void Confirm_NoCancelsWithoutRunningTheAction()
    {
        var vm = new MainViewModelBuilder().Build();
        bool ran = false;

        vm.ShowConfirmationDialog("Title", "Message", () => ran = true);
        vm.AnswerPrompt(vm.PromptSeq, confirm: false, checkboxChecked: false);

        Assert.That(ran, Is.False);
        Assert.That(vm.IsPromptPending, Is.False);
    }

    [Test]
    public void Confirm_CheckboxAnswerReachesTheAction()
    {
        var vm = new MainViewModelBuilder().Build();
        bool? got = null;

        vm.ShowConfirmationDialog("Delete Field", "Sure?", "Also delete jobs", true, b => got = b);
        vm.AnswerPrompt(vm.PromptSeq, confirm: true, checkboxChecked: false);

        Assert.That(got, Is.False);
    }

    [Test]
    public void StaleAnswer_DoesNotAnswerANewerPrompt()
    {
        var vm = new MainViewModelBuilder().Build();
        bool firstRan = false, secondRan = false;

        vm.ShowConfirmationDialog("First", "1", () => firstRan = true);
        int staleSeq = vm.PromptSeq;
        vm.ShowConfirmationDialog("Second", "2", () => secondRan = true);

        Assert.That(vm.AnswerPrompt(staleSeq, confirm: true, checkboxChecked: false), Is.False);
        Assert.That(firstRan || secondRan, Is.False);
        Assert.That(vm.IsPromptPending, Is.True);
    }

    [Test]
    public void Error_IsPending_AndAnyAnswerDismissesIt()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ShowErrorDialog("No Boundary", "Load a field with a boundary first.");
        Assert.That(vm.State.UI.ActiveDialog, Is.EqualTo(DialogType.Error));

        vm.AnswerPrompt(vm.PromptSeq, confirm: true, checkboxChecked: false);
        Assert.That(vm.IsPromptPending, Is.False);
    }

    [Test]
    public void ReportFailure_SetsStatusAndRaisesTheEvent()
    {
        var vm = new MainViewModelBuilder().Build();
        string? heard = null;
        vm.FailureReported += m => heard = m;

        vm.DeleteContourTrackCommand!.Execute(null); // nothing selected

        Assert.That(heard, Is.EqualTo("No track selected"));
        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void DeleteAllTracksConfirmed_DeletesWithoutAHostPrompt()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-vmtracks-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var builder = new MainViewModelBuilder();
            builder.FieldService.ActiveField.Returns(new Field { Name = "F", DirectoryPath = dir });
            var vm = builder.Build();
            vm.SavedTracks.Add(Track.FromABLine("AB1", new Vec3(0, 0, 0), new Vec3(0, 100, 0)));
            vm.SavedTracks.Add(Track.FromABLine("AB2", new Vec3(10, 0, 0), new Vec3(10, 100, 0)));

            vm.DeleteAllTracksConfirmed();

            Assert.That(vm.SavedTracks, Is.Empty);
            Assert.That(vm.IsPromptPending, Is.False, "the browser already confirmed");
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
    private sealed class LinesLogger : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    /// <summary>Fork: a bug report says which dialogs the operator saw and what was pressed.</summary>
    [Test]
    public void Dialogs_and_the_answers_go_into_the_log()
    {
        var vm = new MainViewModelBuilder().Build();
        var lines = new LinesLogger();
        vm.OperatorLog = new AgOpenWeb.Services.Logging.OperatorMessageLog(lines);

        vm.ShowConfirmationDialog("Steer Wizard", "Leave the wizard?", "Leave", "Stay", () => { });
        vm.AnswerPrompt(vm.PromptSeq, confirm: false, checkboxChecked: false);
        vm.ShowConfirmationDialog("Delete Field", "Sure?", "Also delete jobs", true, _ => { });
        vm.AnswerPrompt(vm.PromptSeq, confirm: true, checkboxChecked: false);
        vm.ShowErrorDialog("No field", "Open a field first");
        vm.AnswerPrompt(vm.PromptSeq, confirm: true, checkboxChecked: false);

        Assert.That(lines.Lines, Is.EqualTo(new[]
        {
            "[Screen] Dialog: Steer Wizard - Leave the wizard?",
            "[Screen] Dialog answered: Stay",
            "[Screen] Dialog: Delete Field - Sure?",
            "[Screen] Dialog answered: Confirm (not ticked: Also delete jobs)",
            "[Screen] Dialog: No field - Open a field first",
            "[Screen] Dialog answered: OK",
        }));
    }
}
