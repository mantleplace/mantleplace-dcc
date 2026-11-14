// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class FMantlePlaceImportSteps;
class STextBlock;
class SVerticalBox;
class SWindow;

/**
 * The import window's content: the `Bundle Import` heading and the run's elapsed time, one row per
 * step with its state word (HPS-51) and, for a step that did not run, why; the status line naming the
 * step in flight and how long it has run; an empty slot where the level control goes; `Cancel` and
 * `Close`. Every word it shows is decided by FMantlePlaceImportSteps, which a headless test reads: this
 * only lays them out.
 */
class SMantlePlaceImportWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceImportWindow) {}
		SLATE_ARGUMENT(TSharedPtr<FMantlePlaceImportSteps>, Steps)
		SLATE_EVENT(FSimpleDelegate, OnCancel)
		SLATE_EVENT(FSimpleDelegate, OnClose)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	/** Rebuild the rows from the steps. */
	void Refresh();

	/** The import's closing message, shown under the steps once the run is over. */
	void SetResult(const FString& Message);

private:
	FText GetStatusText() const;
	FText GetRunElapsedText() const;
	bool IsCancelEnabled() const;
	bool IsCloseEnabled() const;

	TSharedPtr<FMantlePlaceImportSteps> Steps;
	FSimpleDelegate OnCancel;
	FSimpleDelegate OnClose;
	TSharedPtr<SVerticalBox> Rows;
	TSharedPtr<STextBlock> ResultText;
};

/**
 * Opens the import window on an import flow's steps and keeps it truthful while the editor's own thread
 * is the one importing.
 *
 * The bundle import is synchronous: from its integrity check to its last step the editor's thread is
 * inside it, and nothing repaints and no click lands unless something ticks Slate. So at each step
 * boundary (every change of the steps) this ticks Slate once, which repaints the rows and delivers a
 * click on Cancel; the click asks the steps to cancel, and the importer stops at its next boundary.
 *
 * Ticking Slate delivers every queued click, not only ours, and a click on the level in the middle of
 * an import would land inside its undo transaction. So for the synchronous run the window is a modal
 * slow-task window, the engine's own arrangement for a cancellable progress dialog: input reaches it
 * alone, and Slate is ticked only while it is the active modal window. Outside that run (the download,
 * which is minutes and asynchronous, and the finished summary) it is an ordinary window and the editor
 * stays usable. A window cannot change modality, so the content moves between two windows at the
 * same place when the run starts and when it ends.
 *
 * Under automation or an unattended run there is no window: the steps run and log the same.
 */
class FMantlePlaceImportWindowHost : public TSharedFromThis<FMantlePlaceImportWindowHost>
{
public:
	/** Open a window on Steps, or return null where no window may open. OnCancel is the import's own cancel. */
	static TSharedPtr<FMantlePlaceImportWindowHost> Open(TSharedRef<FMantlePlaceImportSteps> Steps, FSimpleDelegate OnCancel);

	~FMantlePlaceImportWindowHost();

	/** The import's closing message. */
	void SetResult(const FString& Message);

	/** Close the window, whatever the run is doing (the vault panel is going away). */
	void Close();

private:
	void HandleChanged();
	void Host(bool bModal);
	void TickSlateBetweenSteps();
	void HandleWindowCloseRequested(const TSharedRef<SWindow>& Window);

	TSharedPtr<FMantlePlaceImportSteps> Steps;
	FSimpleDelegate OnCancel;
	TSharedPtr<SMantlePlaceImportWindow> Content;
	TSharedPtr<SWindow> Window;
	bool bModal = false;
	bool bClosed = false;
};
