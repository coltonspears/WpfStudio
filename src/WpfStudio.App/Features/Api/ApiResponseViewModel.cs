using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;

namespace WpfStudio.App.Features.Api;

/// <summary>A response as the response panel shows it, or why there is none.</summary>
public sealed partial class ApiResponseViewModel : ObservableObject
{
    private ApiResponseViewModel(ApiResponse? response, string? error)
    {
        Response = response;
        Error = error;
    }

    public static ApiResponseViewModel From(ApiResponse response) => new(response, null);
    public static ApiResponseViewModel Failed(string error) => new(null, error);

    public ApiResponse? Response { get; }
    public string? Error { get; }
    public bool HasResponse => Response is not null;
    public bool HasError => Error is not null;

    /// <summary>Show the body as received rather than indented.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Body))] public partial bool ShowRaw { get; set; }

    public string StatusText => Response is null ? "Error" : $"{Response.StatusCode} {Response.ReasonPhrase}";
    /// <summary>Success, Redirect, ClientError, ServerError or Error, for the status chip's colour.</summary>
    public string Tone => Response?.StatusCode switch
    {
        >= 200 and < 300 => "Success",
        >= 300 and < 400 => "Redirect",
        >= 400 and < 500 => "ClientError",
        >= 500 => "ServerError",
        _ => "Error",
    };
    public string TimeText => Response is null ? "" : Response.ElapsedMilliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms";
    public string TimeDetail => Response is null ? "" : $"Headers after {Response.HeadersMilliseconds.ToString("N0", CultureInfo.CurrentCulture)} ms, body complete after {Response.ElapsedMilliseconds.ToString("N0", CultureInfo.CurrentCulture)} ms";
    public string SizeText => Response is null ? "" : FormatSize(Response.Body.LongLength) + (Response.Truncated ? " (truncated)" : "");
    public string Body => Response is null ? Error ?? "" : ShowRaw || Response.Pretty.Length == 0 ? Response.Text : Response.Pretty;
    public bool IsJson => Response?.IsJson == true;
    /// <summary>The highlighting the body gets: json, xml or text.</summary>
    public string Language => Response?.IsJson == true ? "json" : Response?.MediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true || Response?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true ? "xml" : "text";
    public IReadOnlyList<KeyValue> Headers => Response?.Headers ?? [];
    public IReadOnlyList<KeyValue> Cookies => Response?.Cookies ?? [];
    public string HeadersLabel => Headers.Count == 0 ? "" : Headers.Count.ToString(CultureInfo.CurrentCulture);
    public string CookiesLabel => Cookies.Count == 0 ? "" : Cookies.Count.ToString(CultureInfo.CurrentCulture);
    public string Warnings => Response is null ? "" : string.Join(Environment.NewLine, Response.Warnings);
    public bool HasWarnings => Response?.Warnings.Count > 0;
    public string Details => Response is null ? "" : $"{Response.HttpVersion} · {Response.Address} · {Response.Proxy}";
    public string SuggestedFileName => (Response?.MediaType ?? "") switch
    {
        var type when type.Contains("json", StringComparison.OrdinalIgnoreCase) => "response.json",
        var type when type.Contains("xml", StringComparison.OrdinalIgnoreCase) => "response.xml",
        var type when type.Contains("html", StringComparison.OrdinalIgnoreCase) => "response.html",
        var type when type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => "response.txt",
        _ => "response.bin",
    };

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.CurrentCulture) + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
        _ => (bytes / (1024d * 1024)).ToString("0.##", CultureInfo.CurrentCulture) + " MB",
    };
}
