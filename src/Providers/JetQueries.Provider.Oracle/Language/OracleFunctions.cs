using JetQueries.Providers.Language;

namespace JetQueries.Provider.Oracle.Language;

/// <summary>
/// The Oracle SQL built-in functions: signatures, what they do (English ~ Türkçe), and parameter values.
/// Functions called without parentheses (SYSDATE, SYSTIMESTAMP, USER…) are left out: inserting them with
/// "()" would be a syntax error. DBMS_* packages are not built-ins of the language and are not listed.
/// </summary>
internal static class OracleFunctions
{
    private static readonly Lazy<FunctionCatalog> Parsed = new(() => FunctionCatalog.Parse(Text));

    public static FunctionCatalog Catalog => Parsed.Value;

    private const string Text = """
        # ---- Value sets ----
        @datefmt: 'YYYY-MM-DD' = ISO date ~ ISO tarih; 'DD.MM.YYYY' = Turkish date ~ Türkçe tarih; 'DD.MM.YYYY HH24:MI:SS' = Turkish date and time ~ Türkçe tarih ve saat; 'YYYY-MM-DD HH24:MI:SS' = ISO date and time ~ ISO tarih ve saat; 'DD/MM/YYYY' = Day/month/year ~ Gün/ay/yıl; 'YYYYMMDD' = Compact date ~ Bitişik tarih; 'HH24:MI:SS' = Time, 24 hours ~ Saat, 24 saat; 'HH24:MI' = Hours and minutes ~ Saat ve dakika; 'YYYY' = Year ~ Yıl; 'Q' = Quarter ~ Çeyrek; 'MM' = Month number ~ Ay numarası; 'MONTH' = Month name ~ Ay adı; 'MON' = Short month name ~ Kısa ay adı; 'DD' = Day of the month ~ Ayın günü; 'DAY' = Day name ~ Gün adı; 'DY' = Short day name ~ Kısa gün adı; 'D' = Day of the week ~ Haftanın günü; 'DDD' = Day of the year ~ Yılın günü; 'HH24' = Hour, 24 hours ~ Saat, 24 saat; 'MI' = Minute ~ Dakika; 'SS' = Second ~ Saniye; 'IW' = ISO week ~ ISO hafta; 'WW' = Week of the year ~ Yılın haftası; 'FF3' = Milliseconds of a timestamp ~ Zaman damgasının milisaniyesi; 'YYYY-MM-DD"T"HH24:MI:SS' = ISO 8601 ~ ISO 8601
        @tocharfmt: 'YYYY-MM-DD' = ISO date ~ ISO tarih; 'DD.MM.YYYY' = Turkish date ~ Türkçe tarih; 'DD.MM.YYYY HH24:MI:SS' = Turkish date and time ~ Türkçe tarih ve saat; 'YYYY-MM-DD HH24:MI:SS' = ISO date and time ~ ISO tarih ve saat; 'YYYYMMDD' = Compact date ~ Bitişik tarih; 'HH24:MI:SS' = Time, 24 hours ~ Saat, 24 saat; 'YYYY' = Year ~ Yıl; 'Q' = Quarter ~ Çeyrek; 'MM' = Month number ~ Ay numarası; 'MONTH' = Month name ~ Ay adı; 'DD' = Day of the month ~ Ayın günü; 'DAY' = Day name ~ Gün adı; 'D' = Day of the week ~ Haftanın günü; 'IW' = ISO week ~ ISO hafta; 'FM999G999G990D00' = Thousands and 2 decimals ~ Binlik ve 2 ondalık; 'FM9999999990' = Integer without padding ~ Boşluksuz tam sayı; 'FM0000' = Zero-padded ~ Sıfırla doldurulmuş; 'L999G999G990D00' = With currency symbol ~ Para birimi simgesiyle
        @numfmt: '9999999990D99' = 2 decimals ~ 2 ondalık; '999G999G990D00' = Thousands and 2 decimals ~ Binlik ve 2 ondalık; '9999999999' = Integer ~ Tam sayı; 'XXXXXXXX' = Hexadecimal ~ Onaltılık
        @truncunit: 'YEAR' = Start of the year ~ Yılın başı; 'Q' = Start of the quarter ~ Çeyreğin başı; 'MONTH' = Start of the month ~ Ayın başı; 'MM' = Start of the month ~ Ayın başı; 'IW' = Start of the ISO week, Monday ~ ISO haftanın başı, pazartesi; 'DAY' = Start of the week, by the territory ~ Haftanın başı, bölgeye göre; 'DD' = Start of the day ~ Günün başı; 'HH' = Start of the hour ~ Saatin başı; 'MI' = Start of the minute ~ Dakikanın başı
        @nls: 'NLS_DATE_LANGUAGE = TURKISH' = Turkish month and day names ~ Türkçe ay ve gün adları; 'NLS_DATE_LANGUAGE = ENGLISH' = English month and day names ~ İngilizce ay ve gün adları; 'NLS_NUMERIC_CHARACTERS = '',.''' = Comma for decimals, dot for thousands ~ Ondalık virgül, binlik nokta; 'NLS_NUMERIC_CHARACTERS = ''.,''' = Dot for decimals, comma for thousands ~ Ondalık nokta, binlik virgül; 'NLS_CURRENCY = ''TL''' = Currency symbol ~ Para birimi simgesi
        @namespace: 'USERENV' = The session environment ~ Oturum ortamı
        @userenv: 'CURRENT_SCHEMA' = Current schema ~ Geçerli şema; 'SESSION_USER' = Logged-in user ~ Oturum açan kullanıcı; 'CURRENT_USER' = User whose privileges are in force ~ Yetkileri geçerli olan kullanıcı; 'DB_NAME' = Database name ~ Veritabanı adı; 'CON_NAME' = Container or PDB name ~ Konteyner ya da PDB adı; 'SERVICE_NAME' = Service connected to ~ Bağlanılan servis; 'INSTANCE_NAME' = Instance name ~ Örnek adı; 'SERVER_HOST' = Database server host ~ Veritabanı sunucusu; 'HOST' = Client machine ~ İstemci makine; 'IP_ADDRESS' = Client IP address ~ İstemci IP adresi; 'OS_USER' = Client operating system user ~ İstemci işletim sistemi kullanıcısı; 'TERMINAL' = Client terminal ~ İstemci terminali; 'MODULE' = Application module ~ Uygulama modülü; 'ACTION' = Application action ~ Uygulama eylemi; 'CLIENT_IDENTIFIER' = Client identifier ~ İstemci kimliği; 'CLIENT_INFO' = Client information ~ İstemci bilgisi; 'SID' = Session id ~ Oturum kimliği; 'LANGUAGE' = Session language and character set ~ Oturum dili ve karakter kümesi; 'NLS_DATE_FORMAT' = Default date format ~ Varsayılan tarih biçimi; 'ISDBA' = TRUE when connected as DBA ~ DBA olarak bağlıysa TRUE
        @regexpmatch: 'i' = Case-insensitive ~ Büyük-küçük harf duyarsız; 'c' = Case-sensitive ~ Büyük-küçük harf duyarlı; 'n' = Dot matches a line break too ~ Nokta satır sonunu da eşler; 'm' = Each line separately, multiline ~ Her satır ayrı, çok satırlı; 'x' = Ignore white space in the pattern ~ Desendeki boşlukları yok say
        @extractfield: YEAR = Year ~ Yıl; MONTH = Month ~ Ay; DAY = Day ~ Gün; HOUR = Hour ~ Saat; MINUTE = Minute ~ Dakika; SECOND = Second ~ Saniye; TIMEZONE_HOUR = Time zone hours ~ Saat dilimi saati; TIMEZONE_MINUTE = Time zone minutes ~ Saat dilimi dakikası; TIMEZONE_REGION = Time zone region ~ Saat dilimi bölgesi
        @jsonpath: '$' = The whole document ~ Belgenin tamamı; '$.name' = A property ~ Bir özellik; '$.items[0]' = First array element ~ Dizinin ilk öğesi; '$.items[*].id' = A property of every element ~ Her öğenin bir özelliği; '$.a.b' = A nested property ~ İç içe özellik
        @weekday: 'MONDAY' = Monday, in the session date language ~ Pazartesi, oturumun tarih dilinde; 'TUESDAY' = Tuesday ~ Salı; 'WEDNESDAY' = Wednesday ~ Çarşamba; 'THURSDAY' = Thursday ~ Perşembe; 'FRIDAY' = Friday ~ Cuma; 'SATURDAY' = Saturday ~ Cumartesi; 'SUNDAY' = Sunday ~ Pazar
        @dsunit: 'DAY' = Days ~ Gün; 'HOUR' = Hours ~ Saat; 'MINUTE' = Minutes ~ Dakika; 'SECOND' = Seconds ~ Saniye
        @ymunit: 'YEAR' = Years ~ Yıl; 'MONTH' = Months ~ Ay
        @timezone: 'Europe/Istanbul' = Turkey ~ Türkiye; 'UTC' = Coordinated Universal Time ~ Eşgüdümlü Evrensel Saat; '+03:00' = Fixed offset ~ Sabit fark; 'Europe/London' = United Kingdom ~ Birleşik Krallık
        @hashmethod: 'SHA256' = SHA-256; 'SHA512' = SHA-512; 'SHA384' = SHA-384; 'SHA1' = SHA-1, the default, deprecated ~ SHA-1, varsayılan, eskimiş; 'MD5' = MD5, deprecated ~ MD5, eskimiş
        @ntilebuckets: 2 = Halves ~ Yarılar; 4 = Quartiles ~ Çeyrekler; 10 = Deciles ~ Ondalık dilimler; 100 = Percentiles ~ Yüzdelik dilimler

        # ---- Dates and times ----
        TO_DATE(text VARCHAR2, [format @datefmt], [nls @nls]) : DATE -- Text to a date with a format mask ~ Metni biçim maskesiyle tarihe çevirir
        TO_TIMESTAMP(text VARCHAR2, [format @datefmt], [nls @nls]) : TIMESTAMP -- Text to a timestamp with a format mask ~ Metni biçim maskesiyle zaman damgasına çevirir
        TO_TIMESTAMP_TZ(text VARCHAR2, [format @datefmt], [nls @nls]) : TIMESTAMP WITH TIME ZONE -- Text to a timestamp with time zone ~ Metni saat dilimli zaman damgasına çevirir
        TRUNC(date DATE, [format @truncunit]) : DATE -- Cuts a date down to a unit, the day by default ~ Tarihi bir birime indirger, varsayılan gün
        TRUNC(n NUMBER, [decimals INTEGER]) : NUMBER -- Cuts a number to a number of decimals ~ Sayıyı belirtilen ondalıkta keser
        ROUND(date DATE, [format @truncunit]) : DATE -- Rounds a date to a unit, the day by default ~ Tarihi bir birime yuvarlar, varsayılan gün
        ROUND(n NUMBER, [decimals INTEGER]) : NUMBER -- Rounds to a number of decimals ~ Belirtilen ondalığa yuvarlar
        ADD_MONTHS(date DATE, months INTEGER) : DATE -- Adds months; the last day stays the last day ~ Ay ekler; ayın son günü son gün kalır
        MONTHS_BETWEEN(date1 DATE, date2 DATE) : NUMBER -- Months from date2 to date1, with a fraction ~ date2 ile date1 arasındaki ay sayısı, kesirli
        LAST_DAY(date DATE) : DATE -- Last day of the month ~ Ayın son günü
        NEXT_DAY(date DATE, weekday @weekday) : DATE -- First given weekday after the date ~ Tarihten sonraki ilk belirtilen gün
        EXTRACT(field @extractfield) : NUMBER -- A part of a date or interval, written EXTRACT(YEAR FROM date) for the year ~ EXTRACT(YEAR FROM tarih) yazımıyla tarih ya da aralığın bir parçası
        NUMTODSINTERVAL(n NUMBER, unit @dsunit) : INTERVAL DAY TO SECOND -- A number of days, hours, minutes or seconds as an interval ~ Gün, saat, dakika ya da saniye sayısını aralığa çevirir
        NUMTOYMINTERVAL(n NUMBER, unit @ymunit) : INTERVAL YEAR TO MONTH -- A number of years or months as an interval ~ Yıl ya da ay sayısını aralığa çevirir
        FROM_TZ(timestamp TIMESTAMP, time_zone @timezone) : TIMESTAMP WITH TIME ZONE -- Gives a timestamp a time zone ~ Zaman damgasına saat dilimi verir
        SYS_EXTRACT_UTC(timestamp TIMESTAMP WITH TIME ZONE) : TIMESTAMP -- The UTC time of a timestamp with time zone ~ Saat dilimli zaman damgasının UTC karşılığı
        TZ_OFFSET(time_zone @timezone) : VARCHAR2 -- Offset of a time zone from UTC now ~ Saat diliminin şu anki UTC farkı

        # ---- Conversion ----
        TO_CHAR(value any, [format @tocharfmt], [nls @nls]) : VARCHAR2 -- A date or number as text with a format mask ~ Tarih ya da sayıyı biçim maskesiyle metne çevirir
        TO_NUMBER(text VARCHAR2, [format @numfmt], [nls @nls]) : NUMBER -- Text to a number ~ Metni sayıya çevirir
        CAST(expression any) : any -- Converts to a type; write CAST(x AS NUMBER), DEFAULT NULL ON CONVERSION ERROR from 12.2 ~ Türe çevirir; yazım CAST(x AS NUMBER), 12.2 ile DEFAULT NULL ON CONVERSION ERROR
        VALIDATE_CONVERSION(expression any) : NUMBER -- Written VALIDATE_CONVERSION(x AS DATE, 'DD.MM.YYYY'), 1 when the conversion would succeed ~ VALIDATE_CONVERSION(x AS DATE, 'DD.MM.YYYY') yazımıyla, dönüşüm başarılı olacaksa 1
        TO_CLOB(text any) : CLOB -- Text to a CLOB ~ Metni CLOB değerine çevirir
        CHR(code NUMBER) : VARCHAR2 -- The character of a code; CHR(10) is a line break ~ Kodun karakteri; CHR(10) satır sonudur
        ASCII(text VARCHAR2) : NUMBER -- Code of the first character ~ İlk karakterin kodu
        RAWTOHEX(raw RAW) : VARCHAR2 -- Bytes as hexadecimal text ~ Baytları onaltılık metne çevirir
        HEXTORAW(text VARCHAR2) : RAW -- Hexadecimal text to bytes ~ Onaltılık metni bayta çevirir

        # ---- NULL and choices ----
        NVL(expr1 any, expr2 any) : any -- expr2 when expr1 is NULL ~ expr1 NULL ise expr2
        NVL2(expr any, if_not_null any, if_null any) : any -- One value when not NULL, another when NULL ~ NULL değilse bir değer, NULL ise diğeri
        COALESCE(expr any, expr any, ...) : any -- First value that is not NULL ~ NULL olmayan ilk değer
        NULLIF(expr1 any, expr2 any) : any -- NULL when the two are equal, else expr1 ~ İkisi eşitse NULL, değilse expr1
        DECODE(expr any, search any, result any, ...) : any -- Compares with each search and returns its result; a last odd value is the default ~ Her aramayla karşılaştırır, eşleşenin sonucunu verir; tek kalan son değer varsayılandır
        GREATEST(expr any, expr any, ...) : any -- The largest value ~ En büyük değer
        LEAST(expr any, expr any, ...) : any -- The smallest value ~ En küçük değer

        # ---- Strings ----
        SUBSTR(text VARCHAR2, position INTEGER, [length INTEGER]) : VARCHAR2 -- Part of a string; a negative position counts from the end ~ Metnin bir parçası; negatif konum sondan sayar
        INSTR(text VARCHAR2, search VARCHAR2, [position INTEGER], [occurrence INTEGER]) : NUMBER -- Position of a string in another, 0 when missing ~ Bir metnin diğerindeki konumu, yoksa 0
        LENGTH(text VARCHAR2) : NUMBER -- Number of characters; NULL for an empty string ~ Karakter sayısı; boş metin için NULL
        LENGTHB(text VARCHAR2) : NUMBER -- Number of bytes ~ Bayt sayısı
        UPPER(text VARCHAR2) : VARCHAR2 -- Upper case ~ Büyük harf
        LOWER(text VARCHAR2) : VARCHAR2 -- Lower case ~ Küçük harf
        INITCAP(text VARCHAR2) : VARCHAR2 -- First letter of each word in upper case ~ Her kelimenin ilk harfi büyük
        NLS_UPPER(text VARCHAR2, [nls VARCHAR2]) : VARCHAR2 -- Upper case by a language; NLS_SORT = XTURKISH for i and İ ~ Dile göre büyük harf; i ve İ için NLS_SORT = XTURKISH
        TRIM(text VARCHAR2) : VARCHAR2 -- Removes spaces at both ends; TRIM(LEADING 'x' FROM text) trims only the start ~ İki uçtaki boşlukları siler; TRIM(LEADING 'x' FROM metin) ile yalnız baştakini
        LTRIM(text VARCHAR2, [set VARCHAR2]) : VARCHAR2 -- Removes characters of a set at the start, spaces by default ~ Baştaki küme karakterlerini siler, varsayılan boşluk
        RTRIM(text VARCHAR2, [set VARCHAR2]) : VARCHAR2 -- Removes characters of a set at the end, spaces by default ~ Sondaki küme karakterlerini siler, varsayılan boşluk
        LPAD(text VARCHAR2, length INTEGER, [pad VARCHAR2]) : VARCHAR2 -- Pads on the left to a length ~ Soldan belirtilen uzunluğa doldurur
        RPAD(text VARCHAR2, length INTEGER, [pad VARCHAR2]) : VARCHAR2 -- Pads on the right to a length ~ Sağdan belirtilen uzunluğa doldurur
        REPLACE(text VARCHAR2, search VARCHAR2, [replacement VARCHAR2]) : VARCHAR2 -- Replaces every occurrence; removes it without a replacement ~ Her geçişi değiştirir; yenisi verilmezse siler
        TRANSLATE(text VARCHAR2, from_set VARCHAR2, to_set VARCHAR2) : VARCHAR2 -- Replaces characters one by one ~ Karakterleri tek tek değiştirir
        CONCAT(text1 VARCHAR2, text2 VARCHAR2) : VARCHAR2 -- Joins two strings; || joins more ~ İki metni birleştirir; daha fazlası için ||
        REGEXP_LIKE(source VARCHAR2, pattern VARCHAR2, [match @regexpmatch]) : BOOLEAN -- Condition: the text matches a regular expression ~ Koşul: metin düzenli ifadeye uyar
        REGEXP_SUBSTR(source VARCHAR2, pattern VARCHAR2, [position INTEGER], [occurrence INTEGER], [match @regexpmatch], [subexpr INTEGER]) : VARCHAR2 -- The part that matches a regular expression ~ Düzenli ifadeye uyan parça
        REGEXP_INSTR(source VARCHAR2, pattern VARCHAR2, [position INTEGER], [occurrence INTEGER], [return_option INTEGER], [match @regexpmatch], [subexpr INTEGER]) : NUMBER -- Position of a regular expression match ~ Düzenli ifade eşleşmesinin konumu
        REGEXP_REPLACE(source VARCHAR2, pattern VARCHAR2, [replacement VARCHAR2], [position INTEGER], [occurrence INTEGER], [match @regexpmatch]) : VARCHAR2 -- Replaces regular expression matches; \1 refers to a group ~ Düzenli ifade eşleşmelerini değiştirir; \1 bir grubu gösterir
        REGEXP_COUNT(source VARCHAR2, pattern VARCHAR2, [position INTEGER], [match @regexpmatch]) : NUMBER -- Number of regular expression matches ~ Düzenli ifade eşleşme sayısı
        DUMP(expression any, [format INTEGER]) : VARCHAR2 -- Type, length and bytes of a value ~ Değerin türü, uzunluğu ve baytları

        # ---- Numbers ----
        ABS(n NUMBER) : NUMBER -- Absolute value ~ Mutlak değer
        CEIL(n NUMBER) : NUMBER -- Smallest integer not less than the value ~ Değerden küçük olmayan en küçük tam sayı
        FLOOR(n NUMBER) : NUMBER -- Largest integer not greater than the value ~ Değerden büyük olmayan en büyük tam sayı
        MOD(n NUMBER, m NUMBER) : NUMBER -- Remainder of n divided by m ~ n bölü m işleminin kalanı
        REMAINDER(n NUMBER, m NUMBER) : NUMBER -- Remainder with rounding, IEEE style ~ Yuvarlamalı kalan, IEEE tarzı
        POWER(n NUMBER, m NUMBER) : NUMBER -- n to the power m ~ n üzeri m
        SQRT(n NUMBER) : NUMBER -- Square root ~ Karekök
        SIGN(n NUMBER) : NUMBER -- -1, 0 or 1 ~ -1, 0 ya da 1
        EXP(n NUMBER) : NUMBER -- e to the power n ~ e üzeri n
        LN(n NUMBER) : NUMBER -- Natural logarithm ~ Doğal logaritma
        LOG(base NUMBER, n NUMBER) : NUMBER -- Logarithm in a base ~ Verilen tabanda logaritma
        BITAND(a INTEGER, b INTEGER) : NUMBER -- Bitwise AND ~ Bit düzeyinde VE

        # ---- Aggregates ----
        COUNT(expression any) : NUMBER -- Number of rows with COUNT(*), or of values that are not NULL ~ COUNT(*) ile satır sayısı ya da NULL olmayan değer sayısı
        SUM(expression NUMBER) : NUMBER -- Sum of the values ~ Değerlerin toplamı
        AVG(expression NUMBER) : NUMBER -- Average of the values ~ Değerlerin ortalaması
        MIN(expression any) : any -- Smallest value ~ En küçük değer
        MAX(expression any) : any -- Largest value ~ En büyük değer
        LISTAGG(measure VARCHAR2, [delimiter VARCHAR2]) : VARCHAR2 -- Joins the values of a group; add WITHIN GROUP (ORDER BY …) for the order ~ Grubun değerlerini birleştirir; sıra için WITHIN GROUP (ORDER BY …) eklenir
        STDDEV(expression NUMBER) : NUMBER -- Standard deviation ~ Standart sapma
        VARIANCE(expression NUMBER) : NUMBER -- Variance ~ Varyans
        MEDIAN(expression NUMBER) : NUMBER -- Middle value ~ Ortanca değer
        PERCENTILE_CONT(fraction NUMBER) : NUMBER -- Interpolated percentile; add WITHIN GROUP (ORDER BY …) ~ Ara değerli yüzdelik; WITHIN GROUP (ORDER BY …) eklenir
        APPROX_COUNT_DISTINCT(expression any) : NUMBER -- Approximate number of distinct values, fast ~ Farklı değer sayısının hızlı yaklaşık değeri

        # ---- Analytic functions ----
        ROW_NUMBER() : NUMBER -- Row number in the window, written with OVER (ORDER BY …) ~ Penceredeki satır numarası, OVER (ORDER BY …) ile yazılır
        RANK() : NUMBER -- Rank with gaps ~ Boşluklu sıra
        DENSE_RANK() : NUMBER -- Rank without gaps ~ Boşluksuz sıra
        NTILE(buckets @ntilebuckets) : NUMBER -- Number of the bucket the row falls into ~ Satırın düştüğü dilimin numarası
        LAG(expression any, [offset INTEGER], [default any]) : any -- Value from a previous row ~ Önceki satırdaki değer
        LEAD(expression any, [offset INTEGER], [default any]) : any -- Value from a following row ~ Sonraki satırdaki değer
        FIRST_VALUE(expression any) : any -- First value in the window ~ Penceredeki ilk değer
        LAST_VALUE(expression any) : any -- Last value in the window ~ Penceredeki son değer
        NTH_VALUE(expression any, n INTEGER) : any -- The n-th value in the window ~ Penceredeki n. değer
        RATIO_TO_REPORT(expression NUMBER) : NUMBER -- Share of the value in the window total ~ Değerin pencere toplamındaki payı
        CUME_DIST() : NUMBER -- Cumulative distribution, 0 to 1 ~ Kümülatif dağılım, 0 ile 1 arası
        PERCENT_RANK() : NUMBER -- Relative rank, 0 to 1 ~ Göreli sıra, 0 ile 1 arası

        # ---- JSON ----
        JSON_VALUE(expression any, path @jsonpath) : VARCHAR2 -- A scalar from JSON; RETURNING NUMBER and ERROR ON ERROR may follow ~ JSON içinden tek bir değer; ardından RETURNING NUMBER ve ERROR ON ERROR gelebilir
        JSON_QUERY(expression any, path @jsonpath) : VARCHAR2 -- An object or array from JSON ~ JSON içinden nesne ya da dizi
        JSON_EXISTS(expression any, path @jsonpath) : BOOLEAN -- Condition: the path exists in the JSON ~ Koşul: yol JSON içinde var
        JSON_TABLE(expression any, path @jsonpath) : table -- Rows and columns from JSON, with a COLUMNS clause ~ COLUMNS bölümüyle JSON içinden satır ve sütunlar
        JSON_OBJECT(key_value any, ...) : VARCHAR2 -- A JSON object; write 'key' VALUE value or 'key' : value ~ JSON nesnesi; yazım 'anahtar' VALUE değer ya da 'anahtar' : değer
        JSON_ARRAY(value any, ...) : VARCHAR2 -- A JSON array ~ JSON dizisi
        JSON_ARRAYAGG(expression any) : VARCHAR2 -- The values of a group as a JSON array ~ Grubun değerlerini JSON dizisi yapar
        JSON_OBJECTAGG(key_value any) : VARCHAR2 -- Keys and values of a group as a JSON object; write KEY k VALUE v ~ Grubun anahtar ve değerlerini JSON nesnesi yapar; yazım KEY k VALUE v

        # ---- System ----
        SYS_CONTEXT(namespace @namespace, parameter @userenv, [length INTEGER]) : VARCHAR2 -- A value of the session environment ~ Oturum ortamından bir değer
        SYS_GUID() : RAW -- A new globally unique id, 16 bytes ~ Yeni evrensel benzersiz kimlik, 16 bayt
        ORA_HASH(expression any, [max_bucket NUMBER], [seed NUMBER]) : NUMBER -- A hash value, for sampling and bucketing ~ Örnekleme ve dilimleme için özet değer
        STANDARD_HASH(expression any, [method @hashmethod]) : RAW -- A cryptographic hash ~ Kriptografik özet
        SYS_CONNECT_BY_PATH(column any, separator VARCHAR2) : VARCHAR2 -- The path from the root in a CONNECT BY query ~ CONNECT BY sorgusunda kökten gelen yol
        EMPTY_CLOB() : CLOB -- An empty CLOB to write into ~ İçine yazılacak boş CLOB
        EMPTY_BLOB() : BLOB -- An empty BLOB to write into ~ İçine yazılacak boş BLOB
        # ---- Called without brackets ----
        SYSDATE : date -- Current date and time of the database server ~ Veritabanı sunucusunun şu anki tarih ve saati
        SYSTIMESTAMP : timestamp with time zone -- Current timestamp of the server, with fractions and time zone ~ Sunucunun kesirli ve saat dilimli şu anki zamanı
        CURRENT_DATE : date -- Current date and time in the session time zone ~ Oturum saat diliminde şu anki tarih ve saat
        CURRENT_TIMESTAMP : timestamp with time zone -- Current timestamp in the session time zone ~ Oturum saat diliminde şu anki zaman damgası
        LOCALTIMESTAMP : timestamp -- Current timestamp in the session time zone, without the zone ~ Oturum saat diliminde, dilimsiz şu anki zaman damgası
        SESSIONTIMEZONE : varchar2 -- Time zone of the session ~ Oturumun saat dilimi
        DBTIMEZONE : varchar2 -- Time zone of the database ~ Veritabanının saat dilimi
        USER : varchar2 -- Name of the session user ~ Oturum kullanıcısının adı
        UID : number -- Id of the session user ~ Oturum kullanıcısının kimliği
        ROWNUM : number -- Order in which a row was selected (1, 2, …) ~ Satırın seçilme sırası (1, 2, …)
        """;
}
