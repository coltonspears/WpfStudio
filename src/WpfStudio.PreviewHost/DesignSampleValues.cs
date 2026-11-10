using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WpfStudio.PreviewHost;

/// <summary>
/// Plausible sample values chosen from a member's name: "Email" reads as an address, "Price" as money, "IsBusy" as
/// false. The same name and position always give the same value, so a preview does not change between renders.
/// </summary>
internal static class DesignSampleValues
{
    private static readonly string[] FirstNames = ["Ada", "Grace", "Alan", "Katherine", "Linus", "Margaret", "Dennis", "Barbara"];
    private static readonly string[] LastNames = ["Lovelace", "Hopper", "Turing", "Johnson", "Torvalds", "Hamilton", "Ritchie", "Liskov"];
    private static readonly string[] Companies = ["Contoso", "Fabrikam", "Northwind Traders", "Adventure Works", "Tailspin Toys", "Wingtip Toys", "Litware", "Proseware"];
    private static readonly string[] Cities = ["Seattle", "London", "Tokyo", "Berlin", "Sydney", "Toronto", "Paris", "Nairobi"];
    private static readonly string[] Countries = ["United States", "United Kingdom", "Japan", "Germany", "Australia", "Canada", "France", "Kenya"];
    private static readonly string[] Regions = ["North", "South", "East", "West", "Central"];
    private static readonly string[] Streets = ["Main Street", "Oak Avenue", "Harbor Road", "Maple Lane", "Station Square"];
    private static readonly string[] Codenames = ["Aurora", "Borealis", "Cascade", "Driftwood", "Ember", "Fjord", "Garnet", "Harbor"];
    private static readonly string[] Statuses = ["Active", "Pending", "Completed", "Draft", "On hold"];
    private static readonly string[] Categories = ["General", "Design", "Research", "Operations", "Finance"];
    private static readonly string[] Priorities = ["High", "Medium", "Low"];
    private static readonly string[] Currencies = ["USD", "EUR", "GBP", "JPY"];
    private static readonly string[] Cultures = ["en-US", "en-GB", "ja-JP", "de-DE"];
    private static readonly string[] Extensions = ["pdf", "docx", "xlsx", "png", "txt"];
    private static readonly string[] Lorem = ("lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua " +
        "ut enim ad minim veniam quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat").Split(' ');
    private static readonly string[] Palette = ["#FF3B82F6", "#FF10B981", "#FFF59E0B", "#FFEF4444", "#FF8B5CF6", "#FF06B6D4"];

    private static readonly HashSet<string> PersonOwners = Set("person people user customer employee contact member author student patient client owner friend account profile " +
        "player teacher driver guest attendee candidate applicant subscriber participant recipient sender staff worker agent");
    private static readonly HashSet<string> PersonMembers = Set("author owner assignee customer contact user person employee manager sender recipient creator member client " +
        "student teacher driver player reviewer approver host guest speaker artist director editor");
    private static readonly HashSet<string> TitleWords = Set("title subject headline heading caption header label");
    private static readonly HashSet<string> ProseWords = Set("description summary notes note body comment comments message text content details detail bio about remarks " +
        "overview abstract info tooltip hint reason feedback review excerpt preview snippet instructions");
    private static readonly HashSet<string> StatusWords = Set("status state stage phase condition");
    private static readonly HashSet<string> CategoryWords = Set("category type kind group genre department team section tag topic role class");
    private static readonly HashSet<string> PriorityWords = Set("priority severity importance level tier grade");
    private static readonly HashSet<string> ColorWords = Set("color colour brush background foreground fill stroke accent tint");
    private static readonly HashSet<string> PictureWords = Set("image photo picture avatar thumbnail thumb logo cover poster banner portrait screenshot artwork");
    private static readonly HashSet<string> LocationWords = Set("url uri path source src file link");
    private static readonly HashSet<string> MoneyWords = Set("price amount cost total subtotal balance salary revenue fee tax discount budget payment income expense profit worth credit debit sales net gross");
    private static readonly HashSet<string> CountWords = Set("count quantity qty items stock units votes likes views downloads followers visits attempts retries seats guests");
    private static readonly HashSet<string> OrdinalWords = Set("index position order sequence rank row column line page step number no");
    private static readonly HashSet<string> PercentWords = Set("percent percentage progress completion rate");
    private static readonly HashSet<string> FractionWords = Set("ratio fraction opacity probability confidence");
    private static readonly HashSet<string> RatingWords = Set("rating score stars");
    private static readonly HashSet<string> LengthWords = Set("width height size length depth radius thickness margin padding spacing offset");
    private static readonly HashSet<string> DurationWords = Set("duration seconds minutes hours days weeks months timeout interval delay elapsed");
    private static readonly HashSet<string> MeasureWords = Set("weight mass volume distance speed capacity area value");
    private static readonly HashSet<string> DateWords = Set("date time day datetime timestamp when birthday birth dob born due deadline expires expiry expiration created updated modified " +
        "changed joined registered published posted ordered scheduled started ended finished opened closed");
    private static readonly HashSet<string> FutureWords = Set("due deadline expires expiry expiration end until next scheduled delivery ship arrival checkout return ended finished");
    private static readonly HashSet<string> PastWords = Set("start started created joined registered opened since from begin hired published posted ordered purchase checkin");
    private static readonly HashSet<string> ListWords = Set("items children nodes rows entries list collection elements subitems");
    private static readonly HashSet<string> FlagPrefixes = Set("is has can should show shows allow allows supports enable enabled are was did will needs requires uses");
    private static readonly HashSet<string> FalseWords = Set("loading busy error errors empty fail failed failure invalid offline disabled hidden collapsed readonly dirty working " +
        "pending deleted removed running processing refreshing saving syncing uploading downloading locked archived muted blocked expired overdue cancelled canceled " +
        "warning fault faulted closed minimized dragging pressed recording paused stopped unknown indeterminate open opened popup editing");
    private static readonly HashSet<string> VaryingWords = Set("selected checked favorite favourite starred read flagged pinned done completed complete new important urgent subscribed following liked bookmarked");
    private static readonly HashSet<string> TrueWords = Set("has can show shows allow allows supports should enable enabled visible valid online available active loaded ready connected " +
        "authenticated authorized success successful succeeded initialized expanded");

    private static HashSet<string> Set(string words) => new(words.Split(' '), StringComparer.Ordinal);

    /// <summary>Lower-case words of a member name: "CustomerEmailAddress" is customer, email, address; "OrderID" is order, id.</summary>
    public static string[] Words(string member)
    {
        var words = new List<string>();
        int start = 0;
        for (int index = 1; index <= member.Length; index++)
        {
            bool boundary = index == member.Length;
            if (!boundary)
            {
                char previous = member[index - 1], current = member[index];
                boundary = !char.IsLetterOrDigit(current) || !char.IsLetterOrDigit(previous)
                    || char.IsUpper(current) && (char.IsLower(previous) || char.IsDigit(previous))
                    || char.IsUpper(current) && char.IsUpper(previous) && index + 1 < member.Length && char.IsLower(member[index + 1])
                    || char.IsDigit(current) != char.IsDigit(previous);
            }
            if (!boundary) continue;
            string word = member.Substring(start, index - start).Trim('_', '-', ' ', '.');
            if (word.Length > 0) words.Add(word.ToLowerInvariant());
            start = index;
        }
        return [.. words];
    }

    private static bool Has(string[] words, HashSet<string> any) => words.Any(any.Contains);
    private static bool Has(string[] words, params string[] any) => words.Any(word => Array.IndexOf(any, word) >= 0);
    private static T Pick<T>(T[] values, int index, int offset = 0) => values[Math.Abs((index + offset) % values.Length)];

    /// <summary>A stable hash: <see cref="string.GetHashCode()"/> differs from one process to the next.</summary>
    private static int Stable(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in text) hash = (hash ^ character) * 16777619;
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private enum Kind { Text, Flag, Picture, Date, Money, Count, Number, List, Command }

    private static Kind? KindOf(string word) =>
        PictureWords.Contains(word) ? Kind.Picture
        : DateWords.Contains(word) || word is "at" or "on" ? Kind.Date
        : MoneyWords.Contains(word) ? Kind.Money
        : CountWords.Contains(word) || OrdinalWords.Contains(word) || word is "id" or "year" or "age" ? Kind.Count
        : PercentWords.Contains(word) || FractionWords.Contains(word) || RatingWords.Contains(word) || LengthWords.Contains(word) || DurationWords.Contains(word)
            || MeasureWords.Contains(word) || word is "latitude" or "lat" or "longitude" or "lon" or "lng" or "temperature" or "temp" or "min" or "minimum" or "max" or "maximum" ? Kind.Number
        : ListWords.Contains(word) ? Kind.List
        : TitleWords.Contains(word) || ProseWords.Contains(word) || StatusWords.Contains(word) || CategoryWords.Contains(word) || PriorityWords.Contains(word)
            || ColorWords.Contains(word) || word is "name" or "by" or "code" or "key" or "email" or "phone" or "address" or "format" or "unit" or "currency" ? Kind.Text
        : null;

    /// <summary>
    /// What a name most likely holds. English names end in their noun ("ItemCount" is a count, "CountLabel" is text),
    /// so the last word decides before any other word is considered.
    /// </summary>
    private static Kind Classify(string[] words)
    {
        if (words[words.Length - 1] == "command") return Kind.Command;
        if (words.Length > 1 && FlagPrefixes.Contains(words[0])) return Kind.Flag;
        // A picture given by location ("PhotoUrl") is text for a converter to load.
        if (Has(words, PictureWords) && Has(words, LocationWords)) return Kind.Text;
        return KindOf(words[words.Length - 1]) ?? words.Select(KindOf).FirstOrDefault(kind => kind is not null) ?? Kind.Text;
    }

    /// <summary>A picture named outright, or an icon given by location ("IconUrl"). A bare "Icon" is as often a glyph or an enum.</summary>
    private static bool NamesPicture(string[] words) => Has(words, PictureWords) || Has(words, "icon", "img") && Has(words, LocationWords);

    public static string Text(string member, string? owner, int index)
    {
        string[] words = Words(member);
        if (words.Length == 0) return "Sample " + Ordinal(index);
        string joined = string.Concat(words);
        string first = Pick(FirstNames, index), last = Pick(LastNames, index);
        // Unmistakable wherever they stand in the name.
        if (Has(words, "email", "mail") || joined.Contains("email")) return (first + "." + last + "@example.com").ToLowerInvariant();
        if (Has(words, "phone", "mobile", "fax", "telephone", "tel")) return "+1 (555) 01" + (index % 100).ToString("00", CultureInfo.InvariantCulture);
        if (NamesPicture(words)) return DesignSamplePictures.Location(index);
        if (Has(words, "url", "uri", "website", "link", "homepage")) return "https://example.com/" + Pick(Codenames, index).ToLowerInvariant();
        if (Has(words, "ip")) return "192.168.0." + (10 + index).ToString(CultureInfo.InvariantCulture);
        if (joined.Contains("firstname") || joined.Contains("givenname") || Has(words, "forename")) return first;
        if (joined.Contains("lastname") || joined.Contains("familyname") || Has(words, "surname")) return last;
        if (joined.Contains("username") || Has(words, "login", "handle", "alias", "nickname")) return (first + last[0]).ToLowerInvariant();
        if (Has(words, "initials")) return new string([first[0], last[0]]);
        if (Has(words, "zip", "postal", "postcode")) return (98101 + index * 37).ToString(CultureInfo.InvariantCulture);
        if (words[words.Length - 1] == "by" || joined.Contains("fullname") || joined.Contains("displayname")) return first + " " + last;
        // Then the noun the name ends in, then any other word.
        return Text([words[words.Length - 1]], words, owner, index) ?? Text(words, words, owner, index) ?? Humanize(words) + " " + Ordinal(index);
    }

    private static string? Text(string[] scope, string[] words, string? owner, int index)
    {
        if (Has(scope, "company", "organization", "organisation", "employer", "vendor", "supplier", "manufacturer", "brand", "publisher")) return Pick(Companies, index);
        if (Has(scope, "city", "town")) return Pick(Cities, index);
        if (Has(scope, "country", "nation")) return Pick(Countries, index);
        if (Has(scope, "province", "region", "county", "territory")) return Pick(Regions, index);
        if (Has(scope, "street", "address")) return (120 + index * 17).ToString(CultureInfo.InvariantCulture) + " " + Pick(Streets, index);
        if (Has(scope, ColorWords)) return Pick(Palette, index);
        if (Has(scope, "id", "code", "sku", "key", "reference", "serial", "isbn", "barcode"))
            return (Has(scope, "id") ? "" : words[words.Length - 1].ToUpperInvariant() + "-") + (1001 + index).ToString(CultureInfo.InvariantCulture);
        if (Has(scope, "version")) return "1." + index.ToString(CultureInfo.InvariantCulture) + ".0";
        if (Has(scope, "path", "folder", "directory")) return @"C:\Samples\" + Pick(Codenames, index);
        if (Has(scope, "file", "filename", "document", "attachment")) return Pick(Codenames, index) + "." + Pick(Extensions, index);
        if (Has(scope, PersonMembers)) return Pick(FirstNames, index) + " " + Pick(LastNames, index);
        if (Has(scope, "product", "project", "model", "campaign", "workspace")) return Pick(Codenames, index);
        if (Has(scope, "name"))
            return owner is not null && Has(Words(owner), PersonOwners) ? Pick(FirstNames, index) + " " + Pick(LastNames, index) : Pick(Codenames, index);
        if (Has(scope, TitleWords)) return Title(index);
        if (Has(scope, ProseWords)) return Sentence(index);
        if (Has(scope, StatusWords)) return Pick(Statuses, index);
        if (Has(scope, PriorityWords)) return Pick(Priorities, index);
        if (Has(scope, CategoryWords)) return Pick(Categories, index);
        if (Has(scope, "currency")) return Pick(Currencies, index);
        if (Has(scope, "language", "locale", "culture")) return Pick(Cultures, index);
        if (Has(scope, "icon", "glyph", "symbol", "emoji")) return "\u2605";
        string member = string.Concat(words);
        if (scope.Any(word => KindOf(word) == Kind.Date)) return Date(member, words, index).ToString("d", CultureInfo.CurrentCulture);
        if (scope.Any(word => KindOf(word) is Kind.Money)) return Number(member, words, index).ToString("N2", CultureInfo.CurrentCulture);
        if (scope.Any(word => KindOf(word) is Kind.Count or Kind.Number)) return Number(member, words, index).ToString("0.##", CultureInfo.CurrentCulture);
        return null;
    }

    private static string Ordinal(int index) => (index + 1).ToString(CultureInfo.InvariantCulture);

    private static string Humanize(string[] words)
    {
        string text = string.Join(" ", words);
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static string Title(int index)
    {
        int count = 2 + index % 3, start = index * 3 % Lorem.Length;
        string text = string.Join(" ", Enumerable.Range(0, count).Select(offset => Pick(Lorem, start, offset)));
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static string Sentence(int index)
    {
        int count = 9 + index * 2 % 5, start = index * 5 % Lorem.Length;
        string text = string.Join(" ", Enumerable.Range(0, count).Select(offset => Pick(Lorem, start, offset)));
        return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
    }

    public static bool Flag(string member, int index)
    {
        string[] words = Words(member);
        bool negated = Has(words, "not", "no", "non", "never");
        bool value = Has(words, FalseWords) ? false
            : Has(words, VaryingWords) ? index % 3 == 0
            : Has(words, TrueWords);
        return negated ? !value : value;
    }

    public static double Number(string member, int index) => Number(member, Words(member), index);

    private static double Number(string member, string[] words, int index) =>
        words.Length == 0 ? index + 1
        : Number([words[words.Length - 1]], index) ?? Number(words, index) ?? Pick([42d, 17, 8, 128, 5], index, Stable(member) % 5);

    private static double? Number(string[] scope, int index)
    {
        if (Has(scope, "year")) return 2021 + index % 5;
        if (Has(scope, "age")) return Pick([34d, 27, 45, 52, 31], index);
        if (Has(scope, PercentWords)) return Pick([72d, 45, 90, 18, 63], index);
        if (Has(scope, FractionWords)) return Pick([0.72, 0.45, 0.9, 0.18, 0.63], index);
        if (Has(scope, RatingWords)) return Pick([4.5, 3.8, 5.0, 4.2, 2.9], index);
        if (Has(scope, CountWords)) return Pick([3d, 12, 7, 24, 1], index);
        if (Has(scope, MoneyWords)) return Pick([19.99, 249.00, 5.50, 1280.00, 74.25], index);
        if (Has(scope, "id")) return 1001 + index;
        if (Has(scope, OrdinalWords)) return index + 1;
        if (Has(scope, LengthWords)) return Pick([120d, 240, 64, 320, 96], index);
        if (Has(scope, "latitude", "lat")) return Pick([47.61, 51.51, 35.68, 52.52, -33.87], index);
        if (Has(scope, "longitude", "lon", "lng")) return Pick([-122.33, -0.13, 139.69, 13.40, 151.21], index);
        if (Has(scope, "temperature", "temp")) return Pick([21.5, 18.0, 25.5, 12.0, 30.5], index);
        if (Has(scope, DurationWords)) return Pick([30d, 45, 90, 15, 60], index);
        if (Has(scope, "min", "minimum")) return 0;
        if (Has(scope, "max", "maximum")) return 100;
        if (Has(scope, MeasureWords)) return Pick([12.5, 3.75, 48.2, 7.1, 96.4], index);
        return null;
    }

    public static DateTime Date(string member, int index) => Date(member, Words(member), index);

    private static DateTime Date(string member, string[] words, int index)
    {
        _ = member;
        var today = DateTime.Today.AddHours(9.5);
        if (Has(words, "birthday", "birth", "dob", "born")) return new DateTime(1985 + index * 3 % 20, 1 + index * 5 % 12, 1 + index * 7 % 28);
        if (Has(words, FutureWords)) return today.AddDays((index + 1) * 3);
        if (Has(words, PastWords)) return today.AddDays(-(index + 1) * 9);
        return today.AddDays(-index).AddHours(-index * 3);
    }

    public static Color Color(int index) => (Color)ColorConverter.ConvertFromString(Pick(Palette, index));

    public static Brush Brush(int index)
    {
        var brush = new SolidColorBrush(Color(index));
        brush.Freeze();
        return brush;
    }

    public static Guid Guid(string member, int index)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(Stable(member)).CopyTo(bytes, 0);
        BitConverter.GetBytes(index + 1).CopyTo(bytes, 12);
        return new Guid(bytes);
    }

    /// <summary>What an untyped sample item answers for a member it has never been asked for.</summary>
    /// <param name="owner">The member this item itself was the value of, which says whose Name a nested "Name" is.</param>
    public static object? Untyped(string member, string? owner, int index, int depth)
    {
        string[] words = Words(member);
        if (words.Length == 0) return null;
        switch (Classify(words))
        {
            case Kind.Command: return DesignCommand.Instance;
            case Kind.Flag: return Flag(member, index);
            case Kind.Picture: return DesignSamplePictures.Image(index);
            case Kind.Date: return Date(member, words, index);
            case Kind.Money: return (decimal)Number(member, words, index);
            case Kind.Count: return (int)Number(member, words, index);
            case Kind.Number: return Number(member, words, index);
            case Kind.List:
                var list = new System.Collections.ObjectModel.ObservableCollection<object>();
                for (int item = 0; depth < 2 && item < 3; item++) list.Add(new DesignSampleItem(item, depth + 1));
                return list;
            default:
                // Text that can also be walked further: "{Binding Customer}" shows it and "{Binding Customer.Name}" continues through it.
                string text = Text(member, owner, index);
                return depth < 3 ? new DesignSampleItem(index, depth + 1, text, member) : text;
        }
    }
}

/// <summary>
/// Placeholder pictures for sample data: a drawing for image-typed members, and the same sketch as a compiled
/// resource for members that name a picture by location. The files under DesignData are that sketch rendered once.
/// </summary>
internal static class DesignSamplePictures
{
    // Sky and hill of each colorway, in the order of sample1.png to sample4.png.
    private static readonly (string Sky, string Hill)[] Colorways = [("#8B5CF6", "#3B82F6"), ("#06B6D4", "#10B981"), ("#F59E0B", "#EF4444"), ("#3B82F6", "#8B5CF6")];
    private static readonly ImageSource?[] Images = new ImageSource?[Colorways.Length];

    /// <summary>A pack URI of a picture compiled into this assembly, so a string or Uri member converts to an image with no file on disk.</summary>
    public static string Location(int index) =>
        "pack://application:,,,/WpfStudio.PreviewHost;component/designdata/sample" + (Math.Abs(index % Colorways.Length) + 1).ToString(CultureInfo.InvariantCulture) + ".png";

    /// <summary>A frozen landscape sketch in one of the colorways. Called on a dispatcher thread, like every other sample.</summary>
    public static ImageSource Image(int index)
    {
        int slot = Math.Abs(index % Colorways.Length);
        if (Images[slot] is { } cached) return cached;
        var sky = (Color)ColorConverter.ConvertFromString(Colorways[slot].Sky);
        var hill = (Color)ColorConverter.ConvertFromString(Colorways[slot].Hill);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(Lighten(sky, 0.55), Lighten(sky, 0.2), 90), null, new RectangleGeometry(new Rect(0, 0, 160, 120))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Colors.White) { Opacity = 0.85 }, null, new EllipseGeometry(new Point(118, 34), 14, 14)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Lighten(hill, 0.25)), null, Geometry.Parse("M0,120 L0,86 L46,48 L84,92 L104,74 L160,120 Z")));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(hill), null, Geometry.Parse("M0,120 L62,78 L96,104 L124,84 L160,104 L160,120 Z")));
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 160, 120));
        var image = new DrawingImage(group);
        image.Freeze();
        return Images[slot] = image;
    }

    private static Color Lighten(Color color, double amount) => System.Windows.Media.Color.FromRgb(
        (byte)(color.R + (255 - color.R) * amount), (byte)(color.G + (255 - color.G) * amount), (byte)(color.B + (255 - color.B) * amount));
}
