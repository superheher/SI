﻿using SIPackages;
using SIPackages.Core;
using SIQuester.Model;
using SIQuester.ViewModel.Helpers;
using SIQuester.ViewModel.Properties;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;

namespace SIQuester.ViewModel;

/// <summary>
/// Defines a package theme view model.
/// </summary>
public sealed class ThemeViewModel : ItemViewModel<Theme>
{
    /// <summary>
    /// Owner round view model.
    /// </summary>
    public RoundViewModel? OwnerRound { get; set; }

    public override IItemViewModel Owner => OwnerRound;

    /// <summary>
    /// Theme questions.
    /// </summary>
    public ObservableCollection<QuestionViewModel> Questions { get; } = new();

    public override ICommand Add { get; protected set; }

    public override string AddHeader => Resources.AddQuestion;

    public override ICommand Remove { get; protected set; }

    public ICommand Clone { get; private set; }

    /// <summary>
    /// Adds new question.
    /// </summary>
    public ICommand AddQuestion { get; private set; }

    /// <summary>
    /// Adds new question having no data.
    /// </summary>
    public ICommand AddEmptyQuestion { get; private set; }

    public ICommand SortQuestions { get; private set; }

    public ThemeViewModel(Theme theme)
        : base(theme)
    {
        foreach (var question in theme.Questions)
        {
            Questions.Add(new QuestionViewModel(question) { OwnerTheme = this });
        }

        Questions.CollectionChanged += Questions_CollectionChanged;

        Clone = new SimpleCommand(CloneTheme_Executed);
        Remove = new SimpleCommand(RemoveTheme_Executed);
        Add = AddQuestion = new SimpleCommand(AddQuestion_Executed);
        AddEmptyQuestion = new SimpleCommand(AddEmptyQuestion_Executed);
        SortQuestions = new SimpleCommand(SortQuestions_Executed);
    }

    private void Questions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
            case NotifyCollectionChangedAction.Replace:
                for (int i = e.NewStartingIndex; i < e.NewStartingIndex + e.NewItems.Count; i++)
                {
                    if (Questions[i].OwnerTheme != null)
                    {
                        throw new Exception(Resources.ErrorInsertingBindedQuestion);
                    }

                    Questions[i].OwnerTheme = this;
                    Model.Questions.Insert(i, Questions[i].Model);
                }
                break;

            case NotifyCollectionChangedAction.Move:
                var temp = Model.Questions[e.OldStartingIndex];
                Model.Questions.Insert(e.NewStartingIndex, temp);
                Model.Questions.RemoveAt(e.OldStartingIndex + (e.NewStartingIndex < e.OldStartingIndex ? 1 : 0));
                break;

            case NotifyCollectionChangedAction.Remove:
                foreach (QuestionViewModel question in e.OldItems)
                {
                    question.OwnerTheme = null;
                    Model.Questions.RemoveAt(e.OldStartingIndex);

                    OwnerRound?.OwnerPackage?.Document?.ClearLinks(question);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                Model.Questions.Clear();
                foreach (QuestionViewModel question in Questions)
                {
                    question.OwnerTheme = this;
                    Model.Questions.Add(question.Model);
                }
                break;
        }
    }

    private void CloneTheme_Executed(object arg)
    {
        var newTheme = Model.Clone() as Theme;
        var newThemeViewModel = new ThemeViewModel(newTheme);
        OwnerRound.Themes.Add(newThemeViewModel);
        OwnerRound.OwnerPackage.Document.Navigate.Execute(newThemeViewModel);
    }

    private void RemoveTheme_Executed(object arg)
    {
        if (OwnerRound == null)
        {
            return;
        }

        OwnerRound.Themes.Remove(this);
    }

    private void AddQuestion_Executed(object arg)
    {
        try
        {
            var document = OwnerRound.OwnerPackage.Document;
            var price = DetectNextQuestionPrice();

            var question = PackageItemsHelper.CreateQuestion(price);

            var questionViewModel = new QuestionViewModel(question);
            Questions.Add(questionViewModel);

            QDocument.ActivatedObject = questionViewModel.Scenario.FirstOrDefault();
            document.Navigate.Execute(questionViewModel);
        }
        catch (Exception exc)
        {
            PlatformSpecific.PlatformManager.Instance.Inform(exc.Message, true);
        }
    }

    private int DetectNextQuestionPrice()
    {
        var validQuestions = Questions.Where(q => q.Model.Price != Question.InvalidPrice).ToList();
        var questionCount = validQuestions.Count;

        if (questionCount > 1)
        {
            var add = validQuestions[1].Model.Price - validQuestions[0].Model.Price;
            return Math.Max(0, validQuestions[questionCount - 1].Model.Price + add);
        }

        if (questionCount > 0)
        {
            return validQuestions[0].Model.Price * 2;
        }

        return OwnerRound.Model.Type == RoundTypes.Final ? 0 : AppSettings.Default.QuestionBase;
    }

    private void AddEmptyQuestion_Executed(object arg)
    {
        var question = new Question { Price = -1 };
        var questionViewModel = new QuestionViewModel(question);
        Questions.Add(questionViewModel);
    }

    private void SortQuestions_Executed(object arg)
    {
        try
        {
            var document = OwnerRound.OwnerPackage.Document;
            using var change = document.OperationsManager.BeginComplexChange();

            for (int i = 1; i < Questions.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (Questions[i].Model.Price < Questions[j].Model.Price)
                    {
                        Questions.Move(i, j);
                        break;
                    }
                }
            }

            change.Commit();
        }
        catch (Exception exc)
        {
            PlatformSpecific.PlatformManager.Instance.Inform(exc.Message, true);
        }
    }

    protected override void UpdateCosts(CostSetter costSetter)
    {
        var document = OwnerRound.OwnerPackage.Document;
        using var change = document.OperationsManager.BeginComplexChange();

        UpdateCostsCore(costSetter);
        change.Commit();
    }

    public void UpdateCostsCore(CostSetter costSetter)
    {
        for (int i = 0; i < Questions.Count; i++)
        {
            Questions[i].Model.Price = costSetter.BaseValue + costSetter.Increment * i;
        }
    }
}
