// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class FMantlePlaceImportSteps;
class FMantlePlaceLevelChoice;
class SBox;
class STextBlock;
class SVerticalBox;
class SWindow;

class SMantlePlaceImportWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceImportWindow) {}
		SLATE_ARGUMENT(TSharedPtr<FMantlePlaceImportSteps>, Steps)
		SLATE_EVENT(FSimpleDelegate, OnCancel)
		SLATE_EVENT(FSimpleDelegate, OnClose)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	void Refresh();

	void SetResult(const FString& Message);

	void SetLevelChoice(TSharedRef<FMantlePlaceLevelChoice> Choice);

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
	TSharedPtr<SBox> LevelSlot;
	TSharedPtr<FMantlePlaceLevelChoice> LevelChoice;
};

class FMantlePlaceImportWindowHost : public TSharedFromThis<FMantlePlaceImportWindowHost>
{
public:
	static TSharedPtr<FMantlePlaceImportWindowHost> Open(TSharedRef<FMantlePlaceImportSteps> Steps, FSimpleDelegate OnCancel);

	~FMantlePlaceImportWindowHost();

	void SetResult(const FString& Message);

	bool ShowLevelChoice(TSharedRef<FMantlePlaceLevelChoice> Choice);

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
