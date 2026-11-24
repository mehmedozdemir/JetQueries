using JetQueries.Providers.Language;

namespace JetQueries.Provider.Sqlite.Language;

/// <summary>
/// The SQLite built-in functions: signatures, what they do (English ~ Türkçe), and parameter values. The date
/// functions take a time value and any number of modifiers ('start of month', '+1 day', 'localtime'…).
/// </summary>
internal static class SqliteFunctions
{
    private static readonly Lazy<FunctionCatalog> Parsed = new(() => FunctionCatalog.Parse(Text));

    public static FunctionCatalog Catalog => Parsed.Value;

    private const string Text = """
        # ---- Value sets ----
        @timevalue: 'now' = The current time, UTC ~ Şu anki zaman, UTC
        @modifier: 'localtime' = UTC to local time ~ UTC zamanı yerel saate çevirir; 'utc' = Local time to UTC ~ Yerel saati UTC zamanına çevirir; 'start of month' = Start of the month ~ Ayın başı; 'start of year' = Start of the year ~ Yılın başı; 'start of day' = Start of the day ~ Günün başı; '+1 day' = Adds a day ~ Bir gün ekler; '-1 day' = Goes back a day ~ Bir gün geri gider; '+7 days' = Adds a week ~ Bir hafta ekler; '+1 month' = Adds a month ~ Bir ay ekler; '-1 month' = Goes back a month ~ Bir ay geri gider; '+1 year' = Adds a year ~ Bir yıl ekler; '+1 hour' = Adds an hour ~ Bir saat ekler; '+30 minutes' = Adds 30 minutes ~ 30 dakika ekler; 'weekday 1' = Next Monday, or today if Monday ~ Sonraki pazartesi, pazartesiyse bugün; 'unixepoch' = The value is Unix seconds ~ Değer Unix saniyesidir; 'auto' = Unix seconds or Julian day, guessed ~ Unix saniyesi ya da Jülyen günü, tahminle; 'subsec' = Keep milliseconds ~ Milisaniyeleri korur
        @timefmt: '%Y-%m-%d' = ISO date ~ ISO tarih; '%Y-%m-%d %H:%M:%S' = ISO date and time ~ ISO tarih ve saat; '%d.%m.%Y' = Turkish date ~ Türkçe tarih; '%d.%m.%Y %H:%M' = Turkish date and time ~ Türkçe tarih ve saat; '%H:%M:%S' = Time ~ Saat; '%Y' = Year ~ Yıl; '%m' = Month, 01-12 ~ Ay, 01-12; '%d' = Day of the month ~ Ayın günü; '%H' = Hour, 00-24 ~ Saat, 00-24; '%M' = Minute ~ Dakika; '%S' = Second ~ Saniye; '%f' = Seconds with milliseconds ~ Milisaniyeli saniye; '%w' = Day of the week, Sunday is 0 ~ Haftanın günü, pazar 0; '%W' = Week of the year ~ Yılın haftası; '%j' = Day of the year ~ Yılın günü; '%s' = Unix seconds ~ Unix saniyesi; '%Y-%m' = Year and month ~ Yıl ve ay
        @printffmt: '%s' = Text ~ Metin; '%d' = Integer ~ Tam sayı; '%.2f' = Number, 2 decimals ~ Sayı, 2 ondalık; '%05d' = Zero-padded integer ~ Sıfırla doldurulmuş tam sayı; '%,d' = Integer with thousands ~ Binlikli tam sayı; '%q' = Text with quotes doubled ~ Tırnakları ikilenmiş metin; '%Q' = Quoted SQL literal ~ Tırnaklı SQL sabiti; '%x' = Hexadecimal ~ Onaltılık
        @jsonpath: '$' = The whole document ~ Belgenin tamamı; '$.name' = A property ~ Bir özellik; '$.a.b' = A nested property ~ İç içe özellik; '$[0]' = First array element ~ Dizinin ilk öğesi; '$.items[#-1]' = Last array element ~ Dizinin son öğesi
        @ntilebuckets: 2 = Halves ~ Yarılar; 4 = Quartiles ~ Çeyrekler; 10 = Deciles ~ Ondalık dilimler; 100 = Percentiles ~ Yüzdelik dilimler

        # ---- Dates and times ----
        date(time_value @timevalue, [modifier @modifier], ...) : text -- The date as YYYY-MM-DD ~ Tarihi YYYY-MM-DD olarak verir
        time(time_value @timevalue, [modifier @modifier], ...) : text -- The time as HH:MM:SS ~ Saati HH:MM:SS olarak verir
        datetime(time_value @timevalue, [modifier @modifier], ...) : text -- Date and time as YYYY-MM-DD HH:MM:SS ~ Tarih ve saati YYYY-MM-DD HH:MM:SS olarak verir
        julianday(time_value @timevalue, [modifier @modifier], ...) : real -- The Julian day number, good for differences in days ~ Jülyen gün sayısı, gün farkı için elverişli
        unixepoch(time_value @timevalue, [modifier @modifier], ...) : integer -- Unix seconds, SQLite 3.38 ~ Unix saniyesi, SQLite 3.38
        strftime(format @timefmt, time_value @timevalue, [modifier @modifier], ...) : text -- A date formatted with % codes ~ Tarihi % kodlarıyla biçimler
        timediff(time1 text, time2 text) : text -- The difference of two times as +YYYY-MM-DD HH:MM:SS, SQLite 3.43 ~ İki zamanın farkı +YYYY-MM-DD HH:MM:SS olarak, SQLite 3.43

        # ---- Strings ----
        length(value any) : integer -- Number of characters, or bytes of a blob ~ Karakter sayısı ya da blob bayt sayısı
        lower(text text) : text -- Lower case, ASCII letters only ~ Küçük harf, yalnız ASCII harfler
        upper(text text) : text -- Upper case, ASCII letters only ~ Büyük harf, yalnız ASCII harfler
        substr(text text, start integer, [length integer]) : text -- Part of a string; a negative start counts from the end ~ Metnin bir parçası; negatif başlangıç sondan sayar
        substring(text text, start integer, [length integer]) : text -- Same as substr ~ substr ile aynı
        instr(text text, search text) : integer -- Position of a string in another, 0 when missing ~ Bir metnin diğerindeki konumu, yoksa 0
        replace(text text, from text, to text) : text -- Replaces every occurrence ~ Her geçişi değiştirir
        trim(text text, [characters text]) : text -- Removes characters at both ends, spaces by default ~ İki uçtaki karakterleri siler, varsayılan boşluk
        ltrim(text text, [characters text]) : text -- Removes characters at the start ~ Baştaki karakterleri siler
        rtrim(text text, [characters text]) : text -- Removes characters at the end ~ Sondaki karakterleri siler
        printf(format @printffmt, value any, ...) : text -- Formats like C printf ~ C printf gibi biçimler
        format(format @printffmt, value any, ...) : text -- Same as printf ~ printf ile aynı
        concat(value any, value any, ...) : text -- Joins values; NULLs are skipped, SQLite 3.44 ~ Değerleri birleştirir; NULL atlanır, SQLite 3.44
        concat_ws(separator text, value any, ...) : text -- Joins values with a separator, SQLite 3.44 ~ Değerleri ayraçla birleştirir, SQLite 3.44
        char(code integer, ...) : text -- Characters of Unicode code points ~ Unicode kodlarının karakterleri
        unicode(text text) : integer -- Code point of the first character ~ İlk karakterin kod noktası
        hex(value any) : text -- Bytes as hexadecimal text ~ Baytları onaltılık metne çevirir
        quote(value any) : text -- The value as an SQL literal ~ Değeri SQL sabiti olarak yazar

        # ---- NULL and choices ----
        coalesce(value any, value any, ...) : any -- First value that is not NULL ~ NULL olmayan ilk değer
        ifnull(value any, replacement any) : any -- replacement when value is NULL ~ value NULL ise replacement
        nullif(value1 any, value2 any) : any -- NULL when the two are equal, else value1 ~ İkisi eşitse NULL, değilse value1
        iif(condition any, if_true any, if_false any) : any -- One value when the condition holds, another when not ~ Koşul doğruysa bir değer, değilse diğeri

        # ---- Numbers and values ----
        abs(value numeric) : numeric -- Absolute value ~ Mutlak değer
        round(value real, [digits integer]) : real -- Rounds to a number of decimals ~ Belirtilen ondalığa yuvarlar
        ceil(value real) : integer -- Smallest integer not less than the value; math functions ~ Değerden küçük olmayan en küçük tam sayı; matematik fonksiyonları
        floor(value real) : integer -- Largest integer not greater than the value; math functions ~ Değerden büyük olmayan en büyük tam sayı; matematik fonksiyonları
        power(x real, y real) : real -- x to the power y; math functions ~ x üzeri y; matematik fonksiyonları
        sqrt(x real) : real -- Square root; math functions ~ Karekök; matematik fonksiyonları
        random() : integer -- A random 64-bit integer ~ Rastgele 64 bit tam sayı
        randomblob(n integer) : blob -- n random bytes ~ n rastgele bayt
        zeroblob(n integer) : blob -- n zero bytes ~ n sıfır bayt
        typeof(value any) : text -- null, integer, real, text or blob ~ null, integer, real, text ya da blob
        last_insert_rowid() : integer -- Rowid of the last row inserted on this connection ~ Bu bağlantıda eklenen son satırın rowid değeri
        changes() : integer -- Rows changed by the last statement ~ Son komutun değiştirdiği satır sayısı
        total_changes() : integer -- Rows changed since the connection opened ~ Bağlantı açıldığından beri değişen satır sayısı
        sqlite_version() : text -- The SQLite library version ~ SQLite kütüphanesinin sürümü

        # ---- Aggregates ----
        count(value any) : integer -- Number of rows with count(*), or of values that are not NULL ~ count(*) ile satır sayısı ya da NULL olmayan değer sayısı
        sum(value numeric) : numeric -- Sum of the values; NULL when there are none ~ Değerlerin toplamı; hiç yoksa NULL
        total(value numeric) : real -- Sum of the values as real; 0.0 when there are none ~ Değerlerin real toplamı; hiç yoksa 0.0
        avg(value numeric) : real -- Average of the values ~ Değerlerin ortalaması
        min(value any, ...) : any -- Smallest value; with more arguments, the smallest of them ~ En küçük değer; daha çok argümanla onların en küçüğü
        max(value any, ...) : any -- Largest value; with more arguments, the largest of them ~ En büyük değer; daha çok argümanla onların en büyüğü
        group_concat(value text, [separator text]) : text -- Joins the values of a group, comma by default ~ Grubun değerlerini birleştirir, varsayılan virgül
        string_agg(value text, separator text) : text -- Same as group_concat, SQLite 3.44 ~ group_concat ile aynı, SQLite 3.44

        # ---- Window functions ----
        row_number() : integer -- Row number in the window, written with OVER (ORDER BY …) ~ Penceredeki satır numarası, OVER (ORDER BY …) ile yazılır
        rank() : integer -- Rank with gaps ~ Boşluklu sıra
        dense_rank() : integer -- Rank without gaps ~ Boşluksuz sıra
        ntile(buckets @ntilebuckets) : integer -- Number of the bucket the row falls into ~ Satırın düştüğü dilimin numarası
        lag(value any, [offset integer], [default any]) : any -- Value from a previous row ~ Önceki satırdaki değer
        lead(value any, [offset integer], [default any]) : any -- Value from a following row ~ Sonraki satırdaki değer
        first_value(value any) : any -- First value in the window ~ Penceredeki ilk değer
        last_value(value any) : any -- Last value in the window ~ Penceredeki son değer
        nth_value(value any, n integer) : any -- The n-th value in the window ~ Penceredeki n. değer
        percent_rank() : real -- Relative rank, 0 to 1 ~ Göreli sıra, 0 ile 1 arası
        cume_dist() : real -- Cumulative distribution, 0 to 1 ~ Kümülatif dağılım, 0 ile 1 arası

        # ---- JSON ----
        json(json text) : text -- Checks and minifies JSON text ~ JSON metnini doğrular ve sıkıştırır
        json_valid(json text) : integer -- 1 when the text is valid JSON ~ Metin geçerli JSON ise 1
        json_extract(json text, path @jsonpath, ...) : any -- The value at a path; with more paths, an array of them ~ Yoldaki değer; daha çok yolla bunların dizisi
        json_type(json text, [path @jsonpath]) : text -- The JSON type at a path ~ Yoldaki JSON türü
        json_array_length(json text, [path @jsonpath]) : integer -- Number of array elements ~ Dizi öğe sayısı
        json_object(label text, value any, ...) : text -- A JSON object from labels and values ~ Etiket ve değerlerden JSON nesnesi
        json_array(value any, ...) : text -- A JSON array ~ JSON dizisi
        json_set(json text, path @jsonpath, value any, ...) : text -- Sets values at paths, creating them when missing ~ Yollardaki değerleri ayarlar, yoksa oluşturur
        json_insert(json text, path @jsonpath, value any, ...) : text -- Adds values only where the path is missing ~ Yalnız olmayan yollara değer ekler
        json_replace(json text, path @jsonpath, value any, ...) : text -- Replaces values only where the path exists ~ Yalnız var olan yollardaki değerleri değiştirir
        json_remove(json text, path @jsonpath, ...) : text -- Removes values at paths ~ Yollardaki değerleri siler
        json_patch(target text, patch text) : text -- Merges a patch into a JSON object, RFC 7396 ~ Bir yamayı JSON nesnesine birleştirir, RFC 7396
        json_each(json text, [path @jsonpath]) : table -- A row per element or property, one level ~ Her öğe ya da özellik için bir satır, tek düzey
        json_tree(json text, [path @jsonpath]) : table -- A row per element or property, every level ~ Her öğe ya da özellik için bir satır, tüm düzeyler
        json_group_array(value any) : text -- The values of a group as a JSON array ~ Grubun değerlerini JSON dizisi yapar
        json_group_object(label text, value any) : text -- Labels and values of a group as a JSON object ~ Grubun etiket ve değerlerini JSON nesnesi yapar
        # ---- Called without brackets ----
        current_date : text -- Current date, UTC, as YYYY-MM-DD ~ UTC olarak bugünün tarihi, YYYY-AA-GG
        current_time : text -- Current time, UTC, as HH:MM:SS ~ UTC olarak şu anki saat, SS:DD:sn
        current_timestamp : text -- Current date and time, UTC, as YYYY-MM-DD HH:MM:SS ~ UTC olarak şu anki tarih ve saat
        """;
}
