using JetQueries.Providers.Language;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>The T-SQL built-in functions: signatures, what they do (English ~ Türkçe), and parameter values.</summary>
internal static class TSqlFunctions
{
    private static readonly Lazy<FunctionCatalog> Parsed = new(() => FunctionCatalog.Parse(Text));

    public static FunctionCatalog Catalog => Parsed.Value;

    private const string Text = """
        # ---- Value sets ----
        @datepart: year = Year ~ Yıl; quarter = Quarter ~ Çeyrek; month = Month ~ Ay; dayofyear = Day of the year ~ Yılın günü; day = Day ~ Gün; week = Week ~ Hafta; weekday = Day of the week ~ Haftanın günü; hour = Hour ~ Saat; minute = Minute ~ Dakika; second = Second ~ Saniye; millisecond = Millisecond ~ Milisaniye; microsecond = Microsecond ~ Mikrosaniye; nanosecond = Nanosecond ~ Nanosaniye; iso_week = ISO week ~ ISO hafta; tzoffset = Time zone offset ~ Saat dilimi farkı
        @datetype: date = Date only ~ Yalnızca tarih; datetime = Date and time (3 ms) ~ Tarih ve saat (3 ms); datetime2 = Date and time (100 ns) ~ Tarih ve saat (100 ns); datetimeoffset = With time zone ~ Saat dilimli; smalldatetime = To the minute ~ Dakika hassasiyetli; time = Time only ~ Yalnızca saat; int = Integer ~ Tam sayı; bigint = 64-bit integer ~ 64 bit tam sayı; decimal(18,2) = Exact number ~ Kesin sayı; float = Floating point ~ Kayan noktalı; varchar(50) = Text ~ Metin; nvarchar(50) = Unicode text ~ Unicode metin; nvarchar(max) = Long Unicode text ~ Uzun Unicode metin; varbinary(max) = Binary ~ İkili; bit = 0 or 1 ~ 0 ya da 1; uniqueidentifier = GUID ~ GUID; money = Money ~ Para
        @style: 0 = mon dd yyyy hh:miAM ~ ay gg yyyy ss:dd; 1 = mm/dd/yy; 101 = mm/dd/yyyy (US) ~ mm/gg/yyyy (ABD); 102 = yyyy.mm.dd (ANSI); 103 = dd/mm/yyyy (British/French) ~ gg/aa/yyyy; 104 = dd.mm.yyyy (German, Turkish) ~ gg.aa.yyyy (Türkçe); 105 = dd-mm-yyyy (Italian) ~ gg-aa-yyyy; 106 = dd mon yyyy; 108 = hh:mi:ss; 110 = mm-dd-yyyy; 111 = yyyy/mm/dd (Japan) ~ yyyy/aa/gg; 112 = yyyymmdd (ISO basic) ~ yyyyaagg; 113 = dd mon yyyy hh:mi:ss:mmm; 114 = hh:mi:ss:mmm; 120 = yyyy-mm-dd hh:mi:ss (ODBC canonical) ~ yyyy-aa-gg ss:dd:sn; 121 = yyyy-mm-dd hh:mi:ss.mmm; 126 = yyyy-mm-ddThh:mi:ss.mmm (ISO 8601); 127 = ISO 8601 with time zone ~ Saat dilimli ISO 8601
        @format: 'd' = Short date ~ Kısa tarih; 'D' = Long date ~ Uzun tarih; 'g' = Short date and time ~ Kısa tarih ve saat; 'G' = Long date and time ~ Uzun tarih ve saat; 'yyyy-MM-dd' = ISO date ~ ISO tarih; 'dd.MM.yyyy' = Turkish date ~ Türkçe tarih; 'dd.MM.yyyy HH:mm' = Turkish date and time ~ Türkçe tarih ve saat; 'HH:mm:ss' = Time ~ Saat; 'N2' = Number, 2 decimals ~ Sayı, 2 ondalık; 'C' = Currency ~ Para birimi; 'P' = Percent ~ Yüzde; '#,0.00' = Thousands and 2 decimals ~ Binlik ve 2 ondalık; '0000' = Zero-padded ~ Sıfırla doldurulmuş
        @culture: 'tr-TR' = Turkish ~ Türkçe; 'en-US' = English (US) ~ İngilizce (ABD); 'en-GB' = English (UK) ~ İngilizce (BK); 'de-DE' = German ~ Almanca; 'fr-FR' = French ~ Fransızca; 'invariant' = Culture-free ~ Kültürden bağımsız
        @trimchars: ' ' = Spaces ~ Boşluklar; CHAR(9) = Tab ~ Sekme; CHAR(13) + CHAR(10) = Line break ~ Satır sonu
        @jsonpath: '$' = The whole document ~ Belgenin tamamı; '$.name' = A property ~ Bir özellik; '$.items[0]' = First array element ~ Dizinin ilk öğesi; 'strict $.name' = Error when missing ~ Yoksa hata; 'lax $.name' = NULL when missing ~ Yoksa NULL
        @ntilebuckets: 2 = Halves ~ Yarılar; 4 = Quartiles ~ Çeyrekler; 10 = Deciles ~ Ondalık dilimler; 100 = Percentiles ~ Yüzdelik dilimler
        @hashalgorithm: 'SHA2_256' = SHA-256; 'SHA2_512' = SHA-512; 'SHA1' = SHA-1 (deprecated) ~ SHA-1 (eskimiş); 'MD5' = MD5 (deprecated) ~ MD5 (eskimiş)
        @roundfunction: 0 = Round (default) ~ Yuvarla (varsayılan); 1 = Truncate ~ Kırp

        # ---- Dates and times ----
        GETDATE() : datetime -- Current date and time of the server ~ Sunucunun şu anki tarih ve saati
        SYSDATETIME() : datetime2 -- Current date and time, 100 ns precision ~ Şu anki tarih ve saat, 100 ns hassasiyet
        SYSDATETIMEOFFSET() : datetimeoffset -- Current date and time with the time zone offset ~ Saat dilimi farkıyla şu anki tarih ve saat
        GETUTCDATE() : datetime -- Current UTC date and time ~ Şu anki UTC tarih ve saat
        DATEADD(datepart @datepart, number int, date datetime) : datetime -- Adds a number of dateparts to a date ~ Tarihe verilen birimden belirtilen kadar ekler
        DATEDIFF(datepart @datepart, startdate datetime, enddate datetime) : int -- Number of datepart boundaries between two dates ~ İki tarih arasındaki birim sayısı
        DATEDIFF_BIG(datepart @datepart, startdate datetime, enddate datetime) : bigint -- DATEDIFF as bigint ~ bigint dönen DATEDIFF
        DATEPART(datepart @datepart, date datetime) : int -- A part of a date as a number ~ Tarihin bir parçasını sayı olarak verir
        DATENAME(datepart @datepart, date datetime) : nvarchar -- A part of a date as text (month name…) ~ Tarihin bir parçasını metin olarak verir (ay adı…)
        DATETRUNC(datepart @datepart, date datetime) : datetime -- Truncates a date to a datepart (SQL Server 2022) ~ Tarihi verilen birime indirger (SQL Server 2022)
        DATE_BUCKET(datepart @datepart, number int, date datetime, [origin datetime]) : datetime -- Start of the fixed-size bucket a date is in ~ Tarihin düştüğü sabit aralığın başı
        DATEFROMPARTS(year int, month int, day int) : date -- A date from its parts ~ Parçalarından tarih oluşturur
        DATETIME2FROMPARTS(year int, month int, day int, hour int, minute int, seconds int, fractions int, precision int) : datetime2 -- A datetime2 from its parts ~ Parçalarından datetime2 oluşturur
        EOMONTH(start_date date, [month_to_add int]) : date -- Last day of the month ~ Ayın son günü
        YEAR(date datetime) : int -- Year of a date ~ Tarihin yılı
        MONTH(date datetime) : int -- Month of a date ~ Tarihin ayı
        DAY(date datetime) : int -- Day of a date ~ Tarihin günü
        ISDATE(expression nvarchar) : int -- 1 when the text is a valid date ~ Metin geçerli bir tarihse 1
        SWITCHOFFSET(datetimeoffset datetimeoffset, offset nvarchar) : datetimeoffset -- Moves a datetimeoffset to another time zone offset ~ datetimeoffset değerini başka saat dilimi farkına taşır
        TODATETIMEOFFSET(datetime datetime2, offset nvarchar) : datetimeoffset -- Adds a time zone offset ~ Saat dilimi farkı ekler

        # ---- Conversion ----
        CONVERT(data_type @datetype, expression any, [style @style]) : any -- Converts a value; style formats dates ~ Değeri dönüştürür; style tarih biçimini seçer
        TRY_CONVERT(data_type @datetype, expression any, [style @style]) : any -- CONVERT, NULL when it fails ~ CONVERT, başarısızsa NULL
        TRY_CAST(expression any) : any -- CAST, NULL when it fails (write: TRY_CAST(x AS type)) ~ CAST, başarısızsa NULL (yazım: TRY_CAST(x AS tür))
        PARSE(string_value nvarchar) : any -- Text to a date or number in a culture (write: PARSE(x AS type USING 'tr-TR')) ~ Metni bir kültüre göre tarih ya da sayıya çevirir
        TRY_PARSE(string_value nvarchar) : any -- PARSE, NULL when it fails ~ PARSE, başarısızsa NULL
        FORMAT(value any, format @format, [culture @culture]) : nvarchar -- Formats a date or number with a .NET format ~ Tarih ya da sayıyı .NET biçimiyle yazar
        STR(float_expression float, [length int], [decimal int]) : varchar -- A number as right-aligned text ~ Sayıyı sağa yaslı metne çevirir

        # ---- Strings ----
        LEN(string nvarchar) : int -- Number of characters, trailing spaces not counted ~ Karakter sayısı (sondaki boşluklar sayılmaz)
        DATALENGTH(expression any) : int -- Number of bytes ~ Bayt sayısı
        LEFT(string nvarchar, count int) : nvarchar -- The first characters ~ Baştaki karakterler
        RIGHT(string nvarchar, count int) : nvarchar -- The last characters ~ Sondaki karakterler
        SUBSTRING(expression nvarchar, start int, length int) : nvarchar -- Part of a string ~ Metnin bir parçası
        CHARINDEX(expressionToFind nvarchar, expressionToSearch nvarchar, [start_location int]) : int -- Position of a string in another (0 when missing) ~ Bir metnin diğerindeki konumu (yoksa 0)
        PATINDEX(pattern nvarchar, expression nvarchar) : int -- Position of a LIKE pattern ~ LIKE deseninin konumu
        REPLACE(string nvarchar, pattern nvarchar, replacement nvarchar) : nvarchar -- Replaces every occurrence ~ Tüm geçtiği yerleri değiştirir
        STUFF(string nvarchar, start int, length int, replace nvarchar) : nvarchar -- Deletes part of a string and inserts another ~ Metnin bir parçasını silip yerine başka metin koyar
        UPPER(string nvarchar) : nvarchar -- Upper case ~ Büyük harf
        LOWER(string nvarchar) : nvarchar -- Lower case ~ Küçük harf
        LTRIM(string nvarchar, [characters @trimchars]) : nvarchar -- Removes leading spaces (or characters) ~ Baştaki boşlukları (ya da karakterleri) siler
        RTRIM(string nvarchar, [characters @trimchars]) : nvarchar -- Removes trailing spaces (or characters) ~ Sondaki boşlukları (ya da karakterleri) siler
        TRIM(string nvarchar) : nvarchar -- Removes spaces at both ends ~ İki uçtaki boşlukları siler
        CONCAT(value any, value any, ...) : nvarchar -- Joins values; NULL counts as empty ~ Değerleri birleştirir; NULL boş sayılır
        CONCAT_WS(separator nvarchar, value any, value any, ...) : nvarchar -- Joins values with a separator, skipping NULLs ~ Değerleri ayraçla birleştirir, NULL'ları atlar
        STRING_AGG(expression nvarchar, separator nvarchar) : nvarchar -- Joins the values of a group (… WITHIN GROUP (ORDER BY …)) ~ Grubun değerlerini birleştirir
        STRING_SPLIT(string nvarchar, separator nvarchar, [enable_ordinal int]) : table -- Splits a string into rows ~ Metni satırlara böler
        REPLICATE(string nvarchar, count int) : nvarchar -- Repeats a string ~ Metni tekrarlar
        REVERSE(string nvarchar) : nvarchar -- Reverses a string ~ Metni ters çevirir
        SPACE(count int) : varchar -- A number of spaces ~ Belirtilen sayıda boşluk
        CHAR(code int) : char -- The character with an ASCII code ~ ASCII koduna karşılık gelen karakter
        NCHAR(code int) : nchar -- The character with a Unicode code ~ Unicode koduna karşılık gelen karakter
        ASCII(character char) : int -- ASCII code of the first character ~ İlk karakterin ASCII kodu
        UNICODE(character nchar) : int -- Unicode code of the first character ~ İlk karakterin Unicode kodu
        QUOTENAME(name sysname, [quote_char nchar]) : nvarchar -- A name in brackets, safe to put in SQL ~ Adı köşeli parantezle güvenle yazar
        TRANSLATE(string nvarchar, characters nvarchar, translations nvarchar) : nvarchar -- Replaces characters one by one ~ Karakterleri tek tek değiştirir
        SOUNDEX(string varchar) : varchar -- Sound code of a word ~ Kelimenin ses kodu

        # ---- NULLs and logic ----
        ISNULL(check_expression any, replacement_value any) : any -- The replacement when the value is NULL ~ Değer NULL ise yerine konacak değer
        COALESCE(expression any, expression any, ...) : any -- The first value that is not NULL ~ NULL olmayan ilk değer
        NULLIF(expression any, expression any) : any -- NULL when the two values are equal ~ İki değer eşitse NULL
        IIF(condition bit, true_value any, false_value any) : any -- One value or the other by a condition ~ Koşula göre iki değerden biri
        CHOOSE(index int, value any, value any, ...) : any -- The value at a position (1-based) ~ Verilen sıradaki değer (1'den başlar)
        GREATEST(expression any, expression any, ...) : any -- The largest value (SQL Server 2022) ~ En büyük değer (SQL Server 2022)
        LEAST(expression any, expression any, ...) : any -- The smallest value (SQL Server 2022) ~ En küçük değer (SQL Server 2022)

        # ---- Numbers ----
        ABS(numeric numeric) : numeric -- Absolute value ~ Mutlak değer
        ROUND(numeric numeric, length int, [function @roundfunction]) : numeric -- Rounds to a number of decimals ~ Belirtilen ondalığa yuvarlar
        CEILING(numeric numeric) : numeric -- Smallest integer not less than the value ~ Değerden küçük olmayan en küçük tam sayı
        FLOOR(numeric numeric) : numeric -- Largest integer not greater than the value ~ Değerden büyük olmayan en büyük tam sayı
        POWER(float float, y float) : float -- A number to a power ~ Üs alır
        SQRT(float float) : float -- Square root ~ Karekök
        SIGN(numeric numeric) : int -- -1, 0 or 1 ~ -1, 0 ya da 1
        RAND([seed int]) : float -- A random number from 0 to 1 ~ 0 ile 1 arasında rastgele sayı
        NEWID() : uniqueidentifier -- A new GUID ~ Yeni GUID
        ISNUMERIC(expression any) : int -- 1 when the value can be a number ~ Değer sayıya çevrilebiliyorsa 1

        # ---- Aggregates and windows ----
        COUNT(expression any) : int -- Number of rows (COUNT(*)) or of values that are not NULL ~ Satır sayısı ya da NULL olmayan değer sayısı
        COUNT_BIG(expression any) : bigint -- COUNT as bigint ~ bigint dönen COUNT
        SUM(expression numeric) : numeric -- Sum of the values ~ Değerlerin toplamı
        AVG(expression numeric) : numeric -- Average of the values ~ Değerlerin ortalaması
        MIN(expression any) : any -- Smallest value ~ En küçük değer
        MAX(expression any) : any -- Largest value ~ En büyük değer
        STDEV(expression float) : float -- Standard deviation ~ Standart sapma
        ROW_NUMBER() : bigint -- Row number in the window (… OVER (ORDER BY …)) ~ Penceredeki satır numarası
        RANK() : bigint -- Rank with gaps ~ Boşluklu sıra
        DENSE_RANK() : bigint -- Rank without gaps ~ Boşluksuz sıra
        NTILE(buckets @ntilebuckets) : bigint -- Number of the bucket the row falls into ~ Satırın düştüğü dilimin numarası
        LAG(expression any, [offset int], [default any]) : any -- Value from a previous row ~ Önceki satırdaki değer
        LEAD(expression any, [offset int], [default any]) : any -- Value from a following row ~ Sonraki satırdaki değer
        FIRST_VALUE(expression any) : any -- First value in the window ~ Penceredeki ilk değer
        LAST_VALUE(expression any) : any -- Last value in the window ~ Penceredeki son değer

        # ---- JSON ----
        JSON_VALUE(expression nvarchar, path @jsonpath) : nvarchar -- A scalar from a JSON text ~ JSON metninden tek bir değer
        JSON_QUERY(expression nvarchar, [path @jsonpath]) : nvarchar -- An object or array from a JSON text ~ JSON metninden nesne ya da dizi
        JSON_MODIFY(expression nvarchar, path @jsonpath, newValue any) : nvarchar -- Changes a value in a JSON text ~ JSON metnindeki bir değeri değiştirir
        ISJSON(expression nvarchar) : int -- 1 when the text is valid JSON ~ Metin geçerli JSON ise 1
        OPENJSON(jsonExpression nvarchar, [path @jsonpath]) : table -- Rows from a JSON text ~ JSON metninden satırlar

        # ---- System ----
        OBJECT_ID(object_name nvarchar, [object_type char]) : int -- Id of an object by name ~ Adına göre nesne kimliği
        OBJECT_NAME(object_id int, [database_id int]) : sysname -- Name of an object by id ~ Kimliğe göre nesne adı
        DB_NAME([database_id int]) : nvarchar -- Name of the current (or a) database ~ Geçerli (ya da verilen) veritabanının adı
        DB_ID([database_name nvarchar]) : int -- Id of a database ~ Veritabanının kimliği
        SCHEMA_NAME([schema_id int]) : sysname -- Name of a schema ~ Şemanın adı
        SUSER_SNAME() : nvarchar -- Login name ~ Oturum adı
        USER_NAME() : nvarchar -- Database user name ~ Veritabanı kullanıcı adı
        HOST_NAME() : nvarchar -- Name of the client computer ~ İstemci bilgisayarın adı
        APP_NAME() : nvarchar -- Application name of the session ~ Oturumun uygulama adı
        SCOPE_IDENTITY() : numeric -- Last identity value inserted in this scope ~ Bu kapsamda eklenen son identity değeri
        HASHBYTES(algorithm @hashalgorithm, input varbinary) : varbinary -- Hash of a value ~ Değerin özeti
        COMPRESS(expression varbinary) : varbinary -- GZIP-compresses a value ~ Değeri GZIP ile sıkıştırır
        DECOMPRESS(expression varbinary) : varbinary -- Decompresses a GZIP value ~ GZIP değeri açar
        ERROR_MESSAGE() : nvarchar -- Message of the error caught by CATCH ~ CATCH ile yakalanan hatanın mesajı
        ERROR_NUMBER() : int -- Number of the error caught by CATCH ~ CATCH ile yakalanan hatanın numarası
        # ---- Called without brackets ----
        CURRENT_TIMESTAMP : datetime -- Current date and time of the server (ANSI form of GETDATE) ~ Sunucunun şu anki tarih ve saati (GETDATE'in ANSI biçimi)
        CURRENT_USER : sysname -- Name of the current database user ~ Geçerli veritabanı kullanıcısının adı
        SESSION_USER : sysname -- Name of the current database user ~ Geçerli veritabanı kullanıcısının adı
        SYSTEM_USER : sysname -- Login name of the session ~ Oturumun giriş adı
        USER : sysname -- Name of the current database user ~ Geçerli veritabanı kullanıcısının adı
        """;
}
