using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private SessionContextBook contextBook = new();
    private bool contextLoading, contextLoadFailed;
    private Guid contextProfileId;
    private void InitializeClientContexts()
    {
        try { contextBook = new SessionContextStore(dataDirectory).Load(); }
        catch (Exception) { contextLoadFailed = true; ContextStatus.Text = "Saved client briefs could not be read. They are preserved; a general meeting can still start. Restore the client-contexts file from a backup to edit profiles."; }
        PopulateContextPicker(contextBook.SelectedProfileId);
        RefreshCompanionControls();
    }
    private void PopulateContextPicker(Guid selected)
    {
        contextLoading = true;
        ClientPicker.ItemsSource = contextBook.Profiles.OrderBy(p => p.ProfileId != Guid.Empty).ThenBy(p => p.DisplayName).ToArray();
        ClientPicker.SelectedItem = contextBook.Profiles.First(p => p.ProfileId == selected);
        contextLoading = false;
        var knowledgeClient = (KnowledgeClient.SelectedItem as SessionContext)?.ProfileId ?? Guid.Empty;
        KnowledgeClient.ItemsSource = contextBook.Profiles;
        KnowledgeClient.SelectedItem = contextBook.Profiles.FirstOrDefault(p => p.ProfileId == knowledgeClient) ?? contextBook.Profiles[0];
        ShowContext((SessionContext)ClientPicker.SelectedItem);
    }
    private void ShowContext(SessionContext context)
    {
        contextProfileId = context.ProfileId;
        ContextClient.Text = context.Client; ContextProject.Text = context.Project; ContextGoal.Text = context.Goal;
        ContextParticipants.Text = string.Join("\n", context.Participants); ContextVocabulary.Text = string.Join("\n", context.Vocabulary);
        RefreshBriefSummary();
    }
    private void ClientPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || contextLoading || companionBusy || Companion?.Active == true || ClientPicker.SelectedItem is not SessionContext context) return;
        ShowContext(context);
        if (contextLoadFailed) return;
        try { var next = contextBook with { SelectedProfileId = context.ProfileId }; new SessionContextStore(dataDirectory).Save(next); contextBook = next; }
        catch (Exception) { ContextStatus.Text = "The selected client could not be remembered. Existing profiles are preserved."; }
    }
    private void ContextField_Changed(object sender, TextChangedEventArgs e) { if (initialized && MeetingBriefSummary is not null) RefreshBriefSummary(); }
    private void RefreshBriefSummary()
    {
        var name = string.IsNullOrWhiteSpace(ContextClient.Text) ? "General meeting" : ContextClient.Text.Trim();
        MeetingBriefSummary.Text = name + (string.IsNullOrWhiteSpace(ContextProject.Text) ? " · Knowledge page filters" : " · Project: " + ContextProject.Text.Trim())
            + (string.IsNullOrWhiteSpace(ContextGoal.Text) ? "" : " · " + ContextGoal.Text.Trim());
    }
    private SessionContext ReadMeetingContext()
    {
        static string[] Lines(string text) => text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var name = ContextClient.Text.Trim();
        var context = new SessionContext { ProfileId = name.Length == 0 ? Guid.Empty : contextProfileId == Guid.Empty ? Guid.NewGuid() : contextProfileId,
            Client = name, Project = ContextProject.Text.Trim(), Goal = ContextGoal.Text.Trim(), Participants = Lines(ContextParticipants.Text), Vocabulary = Lines(ContextVocabulary.Text) };
        if (!context.IsValid) throw new System.IO.InvalidDataException("Keep the brief within 6,000 characters: up to 50 participant names and 100 vocabulary terms.");
        return context;
    }
    internal SessionContext SaveMeetingContext()
    {
        var context = ReadMeetingContext();
        if (!contextLoadFailed)
        {
            contextBook = new SessionContextStore(dataDirectory).SaveProfile(contextBook, context);
            PopulateContextPicker(context.ProfileId); ContextStatus.Text = "Brief saved. It will stay fixed during the next session.";
        }
        return context.Snapshot();
    }
    private void SaveContext_Click(object sender, RoutedEventArgs e)
    {
        if (companionBusy || Companion?.Active == true || contextLoadFailed) return;
        try { SaveMeetingContext(); } catch (Exception ex) { ContextStatus.Text = "Brief was not saved: " + ex.Message; }
    }
    private void NewContext_Click(object sender, RoutedEventArgs e)
    {
        if (companionBusy || Companion?.Active == true || contextLoadFailed) return;
        contextLoading = true; ClientPicker.SelectedItem = null; contextLoading = false;
        ShowContext(new()); ContextClient.Focus(); ContextStatus.Text = "Enter a client name and save the brief. Existing clients are retained.";
    }
}
