// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Slate/SMantlePlaceImportWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "Framework/Docking/TabManager.h"
#include "HAL/PlatformTime.h"
#include "MantlePlaceEditorStyle.h"
#include "MantlePlaceImportStepsLogic.h"
#include "Misc/App.h"
#include "Misc/AutomationTest.h"
#include "RenderingThread.h"
#include "RHICommandList.h"
#include "Rendering/SlateRenderer.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SSeparator.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SWindow.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "MantlePlaceImportWindow"

namespace
{
using EFont = FMantlePlaceEditorStyle::EFont;

FSlateColor StateColor(EMantlePlaceImportStepState State)
{
	switch (State)
	{
	case EMantlePlaceImportStepState::Failed: return FSlateColor(FLinearColor(0.90f, 0.32f, 0.28f));
	case EMantlePlaceImportStepState::Importing: return FSlateColor::UseForeground();
	case EMantlePlaceImportStepState::Done: return FSlateColor::UseForeground();
	default: return FSlateColor::UseSubduedForeground();
	}
}
}

void SMantlePlaceImportWindow::Construct(const FArguments& InArgs)
{
	Steps = InArgs._Steps;
	OnCancel = InArgs._OnCancel;
	OnClose = InArgs._OnClose;

	using S = FMantlePlaceEditorStyle;
	ChildSlot
	[
		SNew(SBorder)
		.Padding(S::S2)
		[
			SNew(SVerticalBox)

			// The heading the standard fixes for this window, and how long the whole run has taken.
			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 0.f, 0.f, S::S3)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock)
					.Font(S::GetFont(EFont::Title))
					.Text(FText::FromString(FMantlePlaceImportSteps::Heading()))
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock)
					.Font(S::GetFont(EFont::Small))
					.ColorAndOpacity(FSlateColor::UseSubduedForeground())
					.Text(this, &SMantlePlaceImportWindow::GetRunElapsedText)
				]
			]

			// One row per step, in the order they run.
			+ SVerticalBox::Slot().AutoHeight()
			[
				SAssignNew(Rows, SVerticalBox)
			]

			// The step in flight, what it is doing and how long it has run.
			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, S::S3, 0.f, 0.f)
			[
				SNew(STextBlock)
				.Font(S::GetFont(EFont::Small))
				.AutoWrapText(true)
				.Text(this, &SMantlePlaceImportWindow::GetStatusText)
			]

			// Where the level control goes. Empty on purpose: the control that fills it is its own change,
			// and the window keeps its place so that change adds a widget and moves nothing.
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(SBox)
			]

			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, S::S3, 0.f, 0.f)
			[
				SAssignNew(ResultText, STextBlock)
				.Font(S::GetFont(EFont::Small))
				.AutoWrapText(true)
				.Visibility(EVisibility::Collapsed)
			]

			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, S::S2, 0.f, 0.f).HAlign(HAlign_Right)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0.f, 0.f, S::S3, 0.f)
				[
					SNew(SButton)
					.ContentPadding(S::ButtonPaddingMd)
					.Text(FText::FromString(FMantlePlaceImportSteps::CancelWord()))
					.ToolTipText(LOCTEXT("CancelTip", "Stops the import when the step in progress ends. What finished stays in the level, and importing again completes it."))
					.IsEnabled(this, &SMantlePlaceImportWindow::IsCancelEnabled)
					.OnClicked_Lambda([this]()
					{
						OnCancel.ExecuteIfBound();
						return FReply::Handled();
					})
				]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton)
					.ContentPadding(S::ButtonPaddingMd)
					.Text(LOCTEXT("Close", "Close"))
					.IsEnabled(this, &SMantlePlaceImportWindow::IsCloseEnabled)
					.OnClicked_Lambda([this]()
					{
						OnClose.ExecuteIfBound();
						return FReply::Handled();
					})
				]
			]
		]
	];
	Refresh();
}

void SMantlePlaceImportWindow::Refresh()
{
	if (!Rows.IsValid() || !Steps.IsValid())
	{
		return;
	}
	using S = FMantlePlaceEditorStyle;
	Rows->ClearChildren();
	for (const FMantlePlaceImportStep& Step : Steps->Steps())
	{
		Rows->AddSlot().AutoHeight().Padding(0.f, S::S6 / 2.f)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Top)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Font(S::GetFont(EFont::Body))
					.Text(FText::FromString(Step.Name))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Font(S::GetFont(EFont::Small))
					.ColorAndOpacity(FSlateColor::UseSubduedForeground())
					.AutoWrapText(true)
					.Visibility(Step.Reason.IsEmpty() ? EVisibility::Collapsed : EVisibility::Visible)
					.Text(FText::FromString(Step.Reason))
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top).Padding(S::S2, 0.f, 0.f, 0.f)
			[
				SNew(SBox)
				.MinDesiredWidth(80.f)
				[
					SNew(STextBlock)
					.Font(S::GetFont(Step.State == EMantlePlaceImportStepState::Importing ? EFont::Title : EFont::Body))
					.ColorAndOpacity(StateColor(Step.State))
					.Text(FText::FromString(FMantlePlaceImportSteps::StateWord(Step.State)))
				]
			]
		];
	}
}

void SMantlePlaceImportWindow::SetResult(const FString& Message)
{
	if (ResultText.IsValid())
	{
		ResultText->SetText(FText::FromString(Message));
		ResultText->SetVisibility(Message.IsEmpty() ? EVisibility::Collapsed : EVisibility::Visible);
	}
}

FText SMantlePlaceImportWindow::GetStatusText() const
{
	return Steps.IsValid() ? FText::FromString(Steps->StatusLine(FPlatformTime::Seconds())) : FText::GetEmpty();
}

FText SMantlePlaceImportWindow::GetRunElapsedText() const
{
	return Steps.IsValid() ? FText::FromString(Steps->RunElapsed(FPlatformTime::Seconds())) : FText::GetEmpty();
}

bool SMantlePlaceImportWindow::IsCancelEnabled() const
{
	return Steps.IsValid() && !Steps->IsFinished() && !Steps->IsCancelRequested();
}

bool SMantlePlaceImportWindow::IsCloseEnabled() const
{
	return !Steps.IsValid() || Steps->IsFinished();
}

TSharedPtr<FMantlePlaceImportWindowHost> FMantlePlaceImportWindowHost::Open(
	TSharedRef<FMantlePlaceImportSteps> InSteps, FSimpleDelegate InOnCancel)
{
	// No window where nobody is there to see it, or where a modal one would stop a script.
	if (!FSlateApplication::IsInitialized() || !FSlateApplication::Get().CanDisplayWindows()
		|| FApp::IsUnattended() || GIsAutomationTesting || IsRunningCommandlet())
	{
		return nullptr;
	}

	TSharedRef<FMantlePlaceImportWindowHost> Host = MakeShared<FMantlePlaceImportWindowHost>();
	Host->Steps = InSteps;
	Host->OnCancel = InOnCancel;
	TWeakPtr<FMantlePlaceImportWindowHost> Weak = Host;
	Host->Content = SNew(SMantlePlaceImportWindow)
		.Steps(InSteps)
		.OnCancel_Lambda([Weak]()
		{
			if (TSharedPtr<FMantlePlaceImportWindowHost> Pinned = Weak.Pin())
			{
				Pinned->OnCancel.ExecuteIfBound();
			}
		})
		.OnClose_Lambda([Weak]()
		{
			if (TSharedPtr<FMantlePlaceImportWindowHost> Pinned = Weak.Pin())
			{
				Pinned->Close();
			}
		});
	InSteps->OnChanged = [Weak]()
	{
		if (TSharedPtr<FMantlePlaceImportWindowHost> Pinned = Weak.Pin())
		{
			Pinned->HandleChanged();
		}
	};
	Host->Host(InSteps->IsSynchronous());
	return Host;
}

FMantlePlaceImportWindowHost::~FMantlePlaceImportWindowHost()
{
	Close();
}

void FMantlePlaceImportWindowHost::SetResult(const FString& Message)
{
	if (Content.IsValid())
	{
		Content->SetResult(Message);
	}
}

void FMantlePlaceImportWindowHost::Close()
{
	if (bClosed)
	{
		return;
	}
	bClosed = true;
	if (Window.IsValid())
	{
		Window->SetRequestDestroyWindowOverride(FRequestDestroyWindowOverride());
		Window->SetContent(SNullWidget::NullWidget);
		Window->RequestDestroyWindow();
		Window.Reset();
	}
}

void FMantlePlaceImportWindowHost::HandleChanged()
{
	if (bClosed || !Steps.IsValid())
	{
		return;
	}
	if (Steps->IsSynchronous() != bModal)
	{
		Host(Steps->IsSynchronous());
	}
	if (Content.IsValid())
	{
		Content->Refresh();
	}
	if (bModal)
	{
		TickSlateBetweenSteps();
	}
}

void FMantlePlaceImportWindowHost::Host(bool bInModal)
{
	TOptional<FVector2D> Position;
	if (Window.IsValid())
	{
		Position = FVector2D(Window->GetPositionInScreen());
		Window->SetRequestDestroyWindowOverride(FRequestDestroyWindowOverride());
		Window->SetContent(SNullWidget::NullWidget);
		Window->RequestDestroyWindow();
		Window.Reset();
	}

	TSharedRef<SWindow> NewWindow = SNew(SWindow)
		.Title(FText::FromString(FMantlePlaceImportSteps::WindowTitle()))
		.SizingRule(ESizingRule::Autosized)
		.AutoCenter(Position.IsSet() ? EAutoCenter::None : EAutoCenter::PreferredWorkArea)
		.ScreenPosition(Position.Get(FVector2D::ZeroVector))
		.SupportsMaximize(false)
		.SupportsMinimize(false)
		.MinWidth(420.f);
	NewWindow->SetContent(Content.ToSharedRef());
	NewWindow->SetRequestDestroyWindowOverride(
		FRequestDestroyWindowOverride::CreateSP(this, &FMantlePlaceImportWindowHost::HandleWindowCloseRequested));
	Window = NewWindow;
	bModal = bInModal;

	const TSharedPtr<SWindow> Root = FGlobalTabmanager::Get()->GetRootWindow();
	if (bModal)
	{
		// A slow-task modal: it returns at once rather than running a loop of its own, and Slate takes
		// input for it alone until it goes. Hidden until shown, as the engine's own progress dialog is.
		FSlateApplication::Get().AddModalWindow(NewWindow, Root, /*bSlowTaskWindow*/ true);
		NewWindow->ShowWindow();
	}
	else if (Root.IsValid())
	{
		FSlateApplication::Get().AddWindowAsNativeChild(NewWindow, Root.ToSharedRef());
	}
	else
	{
		FSlateApplication::Get().AddWindow(NewWindow);
	}
}

void FMantlePlaceImportWindowHost::TickSlateBetweenSteps()
{
	// The engine's slow-task tick (FeedbackContextEditor): only while this window is the active modal one,
	// so input reaches it alone, and never from inside a Slate tick already running.
	FSlateApplication& Slate = FSlateApplication::Get();
	const TSharedPtr<SWindow> Active = Slate.GetActiveModalWindow();
	if (!Window.IsValid() || !Active.IsValid() || !(Active == Window || Window->IsDescendantOf(Active)) || Slate.IsTicking())
	{
		return;
	}
	Slate.Tick();
	if (GIsRHIInitialized)
	{
		ENQUEUE_RENDER_COMMAND(MantlePlaceImportWindowEndFrame)([](FRHICommandListImmediate& RHICmdList) { RHICmdList.EndFrame(); });
	}
	Slate.GetRenderer()->Sync();
}

void FMantlePlaceImportWindowHost::HandleWindowCloseRequested(const TSharedRef<SWindow>& InWindow)
{
	// The title bar's close button. While the run goes on it is Cancel: a window that vanished while the
	// import carried on would hide the one control that stops it. Once the run is over it closes.
	if (Steps.IsValid() && !Steps->IsFinished())
	{
		OnCancel.ExecuteIfBound();
		return;
	}
	Close();
}

#undef LOCTEXT_NAMESPACE
