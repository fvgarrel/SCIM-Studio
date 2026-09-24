using CommunityToolkit.Mvvm.ComponentModel;
using ScimStudio.App.Localization;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>
/// A view model that shows text from the catalogues. On a change of language every property is announced as changed, so text a view model
/// composed itself - a count, a date, a failure - is written again in the new language without each one tracking what it depends on.
/// </summary>
public abstract class ViewModelBase : ObservableObject, ILocalizable {
    protected ViewModelBase() {
        Localizer.Instance.Register(this);
    }

    protected static Localizer L => Localizer.Instance;

    public virtual void Relocalize() {
        OnPropertyChanged(string.Empty);
    }

    /// <summary>A failure as a person reads it: the SCIM error the server sent, or what kept the request from arriving.</summary>
    /// <param name="failure">The failure.</param>
    protected static string Describe(Exception failure) {
        ArgumentNullException.ThrowIfNull(failure);

        return failure switch {
            ScimException scim => scim.Error.ToString(),
            HttpRequestException http => L.Format("error.transport", http.InnerException?.Message ?? http.Message),
            TaskCanceledException => L.Get("error.timeout"),
            _ => failure.Message,
        };
    }

    protected static string Moment(DateTimeOffset? moment) {
        return moment?.ToLocalTime().ToString("g", L.Culture) ?? "—";
    }
}
