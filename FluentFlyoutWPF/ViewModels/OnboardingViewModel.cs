// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFlyout.Classes.Settings;
using System.Windows;

namespace FluentFlyoutWPF.ViewModels;

public sealed class OnboardingStep
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string ImageSource { get; init; }
}

public class OnboardingViewModel : ObservableObject
{
    private int _currentStepIndex;
    private bool _isLoading = true;
    private bool _isTransitioning;

    public UserSettings Settings => SettingsManager.Current;

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsTransitioning
    {
        get => _isTransitioning;
        set
        {
            if (SetProperty(ref _isTransitioning, value))
            {
                GoBackCommand.NotifyCanExecuteChanged();
                GoNextCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key)?.ToString() ?? key;

    private static IReadOnlyList<OnboardingStep> BuildSteps() =>
    [
        new OnboardingStep
        {
            Title = GetResourceString("MediaFlyoutTitle"),
            Description = GetResourceString("MediaFlyoutDescription"),
            ImageSource = "/Resources/Onboarding/MediaFlyout.png"
        },
        new OnboardingStep
        {
            Title = GetResourceString("VolumeFlyoutTitle"),
            Description = GetResourceString("VolumeFlyoutDescription"),
            ImageSource = "/Resources/FluentFlyoutVolumeDemo.png"
        },
        new OnboardingStep
        {
            Title = GetResourceString("LockKeysCustomizationTitle"),
            Description = GetResourceString("LockKeysDescription"),
            ImageSource = "/Resources/Onboarding/LockKeysFlyout.png"
        },
        new OnboardingStep
        {
            Title = GetResourceString("UnlockFullExperienceText"),
            Description = "",
            ImageSource = "/Resources/Onboarding/Taskbar.png"
        }
    ];

    public IReadOnlyList<OnboardingStep> Steps { get; private set; } = BuildSteps();

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            if (SetProperty(ref _currentStepIndex, value))
            {
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(StepProgressText));
                OnPropertyChanged(nameof(NextButtonText));
                OnPropertyChanged(nameof(IsLastStep));
                OnPropertyChanged(nameof(IsMediaStep));
                OnPropertyChanged(nameof(IsVolumeStep));
                OnPropertyChanged(nameof(IsLockKeysStep));
                OnPropertyChanged(nameof(IsPremiumStep));
                OnPropertyChanged(nameof(CanGoBack));
                GoBackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public OnboardingStep CurrentStep => Steps[CurrentStepIndex];

    public string StepProgressText => string.Format(GetResourceString("OnboardingStepsCounter"), CurrentStepIndex + 1, Steps.Count);

    public string? NextButtonText => IsLastStep ? GetResourceString("Finish") : GetResourceString("Next");

    public bool IsLastStep => CurrentStepIndex >= Steps.Count - 1;

    public bool IsMediaStep => CurrentStepIndex == 0;

    public bool IsVolumeStep => CurrentStepIndex == 1;

    public bool IsLockKeysStep => CurrentStepIndex == 2;

    public bool IsPremiumStep => CurrentStepIndex == 3;

    public bool CanGoBack => CurrentStepIndex > 0;

    public event EventHandler? Completed;

    public IRelayCommand GoBackCommand { get; }

    public IRelayCommand GoNextCommand { get; }

    public IRelayCommand SkipCommand { get; }

    public OnboardingViewModel()
    {
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack && !IsTransitioning);
        GoNextCommand = new RelayCommand(GoNext, () => !IsTransitioning);
        SkipCommand = new RelayCommand(Skip);
        System.ComponentModel.PropertyChangedEventManager.AddHandler(
            FluentFlyout.Classes.Settings.SettingsManager.Current,
            OnSettingsPropertyChanged,
            nameof(UserSettings.AppLanguage));
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Steps = BuildSteps();
        OnPropertyChanged(nameof(Steps));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(StepProgressText));
        OnPropertyChanged(nameof(NextButtonText));
    }

    private void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        CurrentStepIndex--;
    }

    private void GoNext()
    {
        if (IsLastStep)
        {
            SettingsManager.SaveSettings();
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentStepIndex++;
    }

    private void Skip()
    {
        SettingsManager.SaveSettings();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}