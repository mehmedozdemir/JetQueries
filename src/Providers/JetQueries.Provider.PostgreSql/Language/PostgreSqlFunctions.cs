using JetQueries.Providers.Language;

namespace JetQueries.Provider.PostgreSql.Language;

/// <summary>
/// The PostgreSQL built-in functions: signatures, what they do (English ~ Türkçe), and parameter values.
/// SQL-standard words used without parentheses (current_date, current_timestamp, current_user…) are left
/// out: inserting them with "()" would be a syntax error.
/// </summary>
internal static class PostgreSqlFunctions
{
    private static readonly Lazy<FunctionCatalog> Parsed = new(() => FunctionCatalog.Parse(Text));

    public static FunctionCatalog Catalog => Parsed.Value;

    private const string Text = """
        # ---- Value sets ----
        @field: 'year' = Year ~ Yıl; 'quarter' = Quarter ~ Çeyrek; 'month' = Month ~ Ay; 'week' = ISO week ~ ISO hafta; 'day' = Day of the month ~ Ayın günü; 'hour' = Hour ~ Saat; 'minute' = Minute ~ Dakika; 'second' = Second with fraction ~ Kesirli saniye; 'dow' = Day of the week, Sunday is 0 ~ Haftanın günü, pazar 0; 'isodow' = ISO day of the week, Monday is 1 ~ ISO haftanın günü, pazartesi 1; 'doy' = Day of the year ~ Yılın günü; 'epoch' = Seconds since 1970-01-01 UTC ~ 1970-01-01 UTC den beri saniye; 'isoyear' = ISO year ~ ISO yıl; 'decade' = Decade ~ On yıl; 'century' = Century ~ Yüzyıl; 'milliseconds' = Milliseconds ~ Milisaniye; 'microseconds' = Microseconds ~ Mikrosaniye; 'timezone' = Time zone offset in seconds ~ Saniye cinsinden saat dilimi farkı
        @truncfield: 'year' = Start of the year ~ Yılın başı; 'quarter' = Start of the quarter ~ Çeyreğin başı; 'month' = Start of the month ~ Ayın başı; 'week' = Start of the ISO week, Monday ~ ISO haftanın başı, pazartesi; 'day' = Start of the day ~ Günün başı; 'hour' = Start of the hour ~ Saatin başı; 'minute' = Start of the minute ~ Dakikanın başı; 'second' = Whole second ~ Tam saniye; 'milliseconds' = Whole millisecond ~ Tam milisaniye; 'decade' = Start of the decade ~ On yılın başı; 'century' = Start of the century ~ Yüzyılın başı
        @extractfield: year = Year ~ Yıl; quarter = Quarter ~ Çeyrek; month = Month ~ Ay; week = ISO week ~ ISO hafta; day = Day of the month ~ Ayın günü; hour = Hour ~ Saat; minute = Minute ~ Dakika; second = Second with fraction ~ Kesirli saniye; dow = Day of the week, Sunday is 0 ~ Haftanın günü, pazar 0; isodow = ISO day of the week ~ ISO haftanın günü; doy = Day of the year ~ Yılın günü; epoch = Seconds since 1970-01-01 UTC ~ 1970-01-01 UTC den beri saniye
        @datefmt: 'YYYY-MM-DD' = ISO date ~ ISO tarih; 'DD.MM.YYYY' = Turkish date ~ Türkçe tarih; 'DD.MM.YYYY HH24:MI:SS' = Turkish date and time ~ Türkçe tarih ve saat; 'YYYY-MM-DD HH24:MI:SS' = ISO date and time ~ ISO tarih ve saat; 'DD/MM/YYYY' = Day/month/year ~ Gün/ay/yıl; 'YYYYMMDD' = Compact date ~ Bitişik tarih; 'HH24:MI:SS' = Time, 24 hours ~ Saat, 24 saat; 'HH24:MI' = Hours and minutes ~ Saat ve dakika; 'YYYY-MM-DD"T"HH24:MI:SS.MSOF' = ISO 8601 ~ ISO 8601
        @tocharfmt: 'YYYY-MM-DD' = ISO date ~ ISO tarih; 'DD.MM.YYYY' = Turkish date ~ Türkçe tarih; 'DD.MM.YYYY HH24:MI:SS' = Turkish date and time ~ Türkçe tarih ve saat; 'YYYY-MM-DD HH24:MI:SS' = ISO date and time ~ ISO tarih ve saat; 'HH24:MI:SS' = Time, 24 hours ~ Saat, 24 saat; 'YYYY' = Year ~ Yıl; 'Q' = Quarter ~ Çeyrek; 'MM' = Month number ~ Ay numarası; 'TMMonth' = Month name in the session language ~ Oturum dilinde ay adı; 'TMDay' = Day name in the session language ~ Oturum dilinde gün adı; 'DD' = Day of the month ~ Ayın günü; 'IW' = ISO week ~ ISO hafta; 'FM999G999G990D00' = Thousands and 2 decimals ~ Binlik ve 2 ondalık; 'FM0000' = Zero-padded ~ Sıfırla doldurulmuş; 'FM9999999990' = Integer without padding ~ Boşluksuz tam sayı
        @numfmt: '9999999990D99' = 2 decimals ~ 2 ondalık; '999G999G990D00' = Thousands and 2 decimals ~ Binlik ve 2 ondalık; '9999999999' = Integer ~ Tam sayı
        @regexflags: 'g' = Every match, not only the first ~ İlki değil her eşleşme; 'i' = Case-insensitive ~ Büyük-küçük harf duyarsız; 'gi' = Every match, case-insensitive ~ Her eşleşme, harf duyarsız; 'c' = Case-sensitive ~ Büyük-küçük harf duyarlı; 'n' = Newline-sensitive ~ Satır sonuna duyarlı; 'x' = Expanded syntax, white space ignored ~ Genişletilmiş yazım, boşluklar yok sayılır
        @setting: 'search_path' = Schemas searched for unqualified names ~ Şemasız adlar için aranan şemalar; 'TimeZone' = Session time zone ~ Oturum saat dilimi; 'DateStyle' = Date output style ~ Tarih çıktı biçimi; 'application_name' = Application name of the session ~ Oturumun uygulama adı; 'statement_timeout' = Statement time limit ~ Komut süre sınırı; 'lock_timeout' = Lock wait limit ~ Kilit bekleme sınırı; 'work_mem' = Memory per sort or hash ~ Sıralama ya da hash başına bellek; 'client_encoding' = Client character set ~ İstemci karakter kümesi; 'server_version' = Server version ~ Sunucu sürümü; 'lc_messages' = Language of messages ~ Mesaj dili; 'transaction_isolation' = Isolation level ~ Yalıtım düzeyi; 'max_connections' = Connection limit ~ Bağlantı sınırı
        @timezone: 'Europe/Istanbul' = Turkey ~ Türkiye; 'UTC' = Coordinated Universal Time ~ Eşgüdümlü Evrensel Saat; 'Europe/London' = United Kingdom ~ Birleşik Krallık; 'America/New_York' = US Eastern ~ ABD Doğu
        @interval: '1 day' = One day ~ Bir gün; '1 hour' = One hour ~ Bir saat; '15 minutes' = Fifteen minutes ~ On beş dakika; '1 week' = One week ~ Bir hafta; '1 month' = One month ~ Bir ay; '1 year' = One year ~ Bir yıl
        @encoding: 'base64' = Base64; 'hex' = Hexadecimal ~ Onaltılık; 'escape' = Escaped bytes ~ Kaçışlı baytlar
        @jsonpath: '$' = The whole document ~ Belgenin tamamı; '$.name' = A property ~ Bir özellik; '$.items[0]' = First array element ~ Dizinin ilk öğesi; '$.items[*]' = Every array element ~ Dizinin her öğesi; '$.items[*] ? (@.price > 10)' = Elements that pass a filter ~ Filtreye uyan öğeler
        @privilege: 'SELECT' = Read ~ Okuma; 'INSERT' = Insert ~ Ekleme; 'UPDATE' = Update ~ Güncelleme; 'DELETE' = Delete ~ Silme; 'TRUNCATE' = Truncate ~ Boşaltma; 'REFERENCES' = Foreign keys ~ Yabancı anahtar; 'TRIGGER' = Triggers ~ Tetikleyici
        @ntilebuckets: 2 = Halves ~ Yarılar; 4 = Quartiles ~ Çeyrekler; 10 = Deciles ~ Ondalık dilimler; 100 = Percentiles ~ Yüzdelik dilimler

        # ---- Dates and times ----
        now() : timestamptz -- Start of the current transaction ~ Geçerli işlemin başlangıç zamanı
        clock_timestamp() : timestamptz -- The actual current time, changes during a statement ~ Gerçek şu anki zaman, komut içinde de değişir
        statement_timestamp() : timestamptz -- Start of the current statement ~ Geçerli komutun başlangıç zamanı
        date_trunc(field @truncfield, source timestamp, [time_zone @timezone]) : timestamp -- Truncates to a unit ~ Verilen birime indirger
        date_part(field @field, source timestamp) : double precision -- A part of a date or interval ~ Tarih ya da aralığın bir parçası
        extract(field @extractfield) : numeric -- A part of a date or interval, written extract(year FROM ts) for the year ~ extract(year FROM ts) yazımıyla tarih ya da aralığın bir parçası
        date_bin(stride @interval, source timestamp, origin timestamp) : timestamp -- Start of the fixed-size bucket a time is in ~ Zamanın düştüğü sabit aralığın başı
        age(timestamp timestamp, [since timestamp]) : interval -- Time between two dates in years, months and days; from midnight today by default ~ İki tarih arası yıl, ay ve gün; varsayılan bugünün başından
        make_date(year int, month int, day int) : date -- A date from its parts ~ Parçalarından tarih oluşturur
        make_time(hour int, min int, sec double precision) : time -- A time from its parts ~ Parçalarından saat oluşturur
        make_timestamp(year int, month int, day int, hour int, min int, sec double precision) : timestamp -- A timestamp from its parts ~ Parçalarından zaman damgası oluşturur
        make_interval([years int], [months int], [weeks int], [days int], [hours int], [mins int], [secs double precision]) : interval -- An interval; usually written with names, make_interval(days => 3) for three days ~ Aralık oluşturur; genelde adla yazılır, make_interval(days => 3) üç gündür
        justify_interval(span interval) : interval -- Turns 24 hours into days and 30 days into months ~ 24 saati güne, 30 günü aya çevirir
        timezone(zone @timezone, timestamp timestamptz) : timestamp -- The time in another time zone, same as AT TIME ZONE ~ Başka saat dilimindeki zaman, AT TIME ZONE ile aynı
        to_timestamp(text text, format @datefmt) : timestamptz -- Text to a timestamp with a format ~ Metni biçimle zaman damgasına çevirir
        to_timestamp(epoch double precision) : timestamptz -- Unix seconds to a timestamp ~ Unix saniyesini zaman damgasına çevirir
        to_date(text text, format @datefmt) : date -- Text to a date with a format ~ Metni biçimle tarihe çevirir
        generate_series(start any, stop any, [step @interval]) : setof any -- Rows from start to stop: numbers, or times with an interval step ~ Başlangıçtan bitişe satırlar: sayılar ya da aralık adımlı zamanlar

        # ---- Conversion ----
        to_char(value any, format @tocharfmt) : text -- A date or number as text with a format ~ Tarih ya da sayıyı biçimle metne çevirir
        to_number(text text, format @numfmt) : numeric -- Text to a number with a format ~ Metni biçimle sayıya çevirir
        cast(expression any) : any -- Converts to a type, written cast(x AS integer) or x::integer ~ Türe çevirir, cast(x AS integer) ya da x::integer yazılır
        to_regclass(name text) : regclass -- The table of a name, NULL when there is none ~ Adın tablosu, yoksa NULL
        pg_typeof(value any) : regtype -- The type of a value ~ Değerin türü

        # ---- NULL and choices ----
        coalesce(value any, value any, ...) : any -- First value that is not NULL ~ NULL olmayan ilk değer
        nullif(value1 any, value2 any) : any -- NULL when the two are equal, else value1 ~ İkisi eşitse NULL, değilse value1
        greatest(value any, value any, ...) : any -- The largest value, NULLs ignored ~ En büyük değer, NULL değerler yok sayılır
        least(value any, value any, ...) : any -- The smallest value, NULLs ignored ~ En küçük değer, NULL değerler yok sayılır

        # ---- Strings ----
        length(text text) : int -- Number of characters ~ Karakter sayısı
        octet_length(text text) : int -- Number of bytes ~ Bayt sayısı
        lower(text text) : text -- Lower case ~ Küçük harf
        upper(text text) : text -- Upper case ~ Büyük harf
        initcap(text text) : text -- First letter of each word in upper case ~ Her kelimenin ilk harfi büyük
        substring(text text, start int, [count int]) : text -- Part of a string; also substring(text FROM 2 FOR 3) ~ Metnin bir parçası; ayrıca substring(metin FROM 2 FOR 3) yazılabilir
        left(text text, n int) : text -- The first characters; a negative n drops from the end ~ Baştaki karakterler; negatif n sondan atar
        right(text text, n int) : text -- The last characters ~ Sondaki karakterler
        strpos(text text, substring text) : int -- Position of a string in another, 0 when missing ~ Bir metnin diğerindeki konumu, yoksa 0
        starts_with(text text, prefix text) : boolean -- Whether the text starts with the prefix ~ Metin önekle başlıyor mu
        replace(text text, from text, to text) : text -- Replaces every occurrence ~ Her geçişi değiştirir
        translate(text text, from text, to text) : text -- Replaces characters one by one ~ Karakterleri tek tek değiştirir
        btrim(text text, [characters text]) : text -- Removes characters at both ends, spaces by default ~ İki uçtaki karakterleri siler, varsayılan boşluk
        ltrim(text text, [characters text]) : text -- Removes characters at the start ~ Baştaki karakterleri siler
        rtrim(text text, [characters text]) : text -- Removes characters at the end ~ Sondaki karakterleri siler
        lpad(text text, length int, [fill text]) : text -- Pads on the left to a length ~ Soldan belirtilen uzunluğa doldurur
        rpad(text text, length int, [fill text]) : text -- Pads on the right to a length ~ Sağdan belirtilen uzunluğa doldurur
        repeat(text text, count int) : text -- The text repeated ~ Metni tekrarlar
        reverse(text text) : text -- The text backwards ~ Metni tersine çevirir
        concat(value any, value any, ...) : text -- Joins values; NULLs are skipped ~ Değerleri birleştirir; NULL değerler atlanır
        concat_ws(separator text, value any, ...) : text -- Joins values with a separator; NULLs are skipped ~ Değerleri ayraçla birleştirir; NULL değerler atlanır
        format(format text, value any, ...) : text -- Formats like sprintf: %s text, %I identifier, %L literal ~ sprintf gibi biçimler: %s metin, %I tanımlayıcı, %L sabit
        split_part(text text, delimiter text, n int) : text -- The n-th field of a delimited text ~ Ayraçlı metnin n. alanı
        string_to_array(text text, delimiter text, [null_string text]) : text[] -- Splits a text into an array ~ Metni diziye böler
        regexp_replace(source text, pattern text, replacement text, [flags @regexflags]) : text -- Replaces regular expression matches; only the first without 'g' ~ Düzenli ifade eşleşmelerini değiştirir; 'g' olmadan yalnız ilkini
        regexp_match(source text, pattern text, [flags @regexflags]) : text[] -- The groups of the first match ~ İlk eşleşmenin grupları
        regexp_matches(source text, pattern text, [flags @regexflags]) : setof text[] -- A row per match with 'g' ~ 'g' ile her eşleşme için bir satır
        regexp_split_to_table(source text, pattern text, [flags @regexflags]) : setof text -- Splits a text into rows at a regular expression ~ Metni düzenli ifadeyle satırlara böler
        regexp_like(source text, pattern text, [flags @regexflags]) : boolean -- Whether the text matches, PostgreSQL 15 ~ Metin uyuyor mu, PostgreSQL 15
        chr(code int) : text -- The character of a code ~ Kodun karakteri
        ascii(text text) : int -- Code of the first character ~ İlk karakterin kodu
        md5(text text) : text -- MD5 hash as hexadecimal ~ Onaltılık MD5 özeti
        encode(bytes bytea, format @encoding) : text -- Bytes as text ~ Baytları metne çevirir
        decode(text text, format @encoding) : bytea -- Text to bytes ~ Metni bayta çevirir
        quote_ident(text text) : text -- Quotes a name when it needs it ~ Adı gerekiyorsa tırnaklar
        quote_literal(text text) : text -- The value as a quoted string literal ~ Değeri tırnaklı metin sabiti yapar

        # ---- Numbers ----
        abs(x numeric) : numeric -- Absolute value ~ Mutlak değer
        ceil(x numeric) : numeric -- Smallest integer not less than the value ~ Değerden küçük olmayan en küçük tam sayı
        floor(x numeric) : numeric -- Largest integer not greater than the value ~ Değerden büyük olmayan en büyük tam sayı
        round(x numeric, [scale int]) : numeric -- Rounds to a number of decimals ~ Belirtilen ondalığa yuvarlar
        trunc(x numeric, [scale int]) : numeric -- Cuts to a number of decimals ~ Belirtilen ondalıkta keser
        mod(y numeric, x numeric) : numeric -- Remainder of y divided by x ~ y bölü x işleminin kalanı
        div(y numeric, x numeric) : numeric -- Integer quotient ~ Tam sayı bölüm
        power(a numeric, b numeric) : numeric -- a to the power b ~ a üzeri b
        sqrt(x numeric) : numeric -- Square root ~ Karekök
        sign(x numeric) : numeric -- -1, 0 or 1 ~ -1, 0 ya da 1
        random() : double precision -- A random number from 0 to 1 ~ 0 ile 1 arasında rastgele sayı
        gen_random_uuid() : uuid -- A new random UUID ~ Yeni rastgele UUID

        # ---- Aggregates ----
        count(expression any) : bigint -- Number of rows with count(*), or of values that are not NULL ~ count(*) ile satır sayısı ya da NULL olmayan değer sayısı
        sum(expression numeric) : numeric -- Sum of the values ~ Değerlerin toplamı
        avg(expression numeric) : numeric -- Average of the values ~ Değerlerin ortalaması
        min(expression any) : any -- Smallest value ~ En küçük değer
        max(expression any) : any -- Largest value ~ En büyük değer
        string_agg(value text, delimiter text) : text -- Joins the values of a group; ORDER BY may follow inside the brackets ~ Grubun değerlerini birleştirir; parantez içinde ORDER BY gelebilir
        array_agg(value any) : any[] -- The values of a group as an array ~ Grubun değerlerini dizi yapar
        bool_and(value boolean) : boolean -- True when every value is true ~ Her değer doğruysa doğru
        bool_or(value boolean) : boolean -- True when any value is true ~ Herhangi bir değer doğruysa doğru
        stddev(expression numeric) : numeric -- Standard deviation ~ Standart sapma
        variance(expression numeric) : numeric -- Variance ~ Varyans
        percentile_cont(fraction double precision) : double precision -- Interpolated percentile; add WITHIN GROUP (ORDER BY …) ~ Ara değerli yüzdelik; WITHIN GROUP (ORDER BY …) eklenir
        mode() : any -- Most frequent value; add WITHIN GROUP (ORDER BY …) ~ En sık değer; WITHIN GROUP (ORDER BY …) eklenir

        # ---- Window functions ----
        row_number() : bigint -- Row number in the window, written with OVER (ORDER BY …) ~ Penceredeki satır numarası, OVER (ORDER BY …) ile yazılır
        rank() : bigint -- Rank with gaps ~ Boşluklu sıra
        dense_rank() : bigint -- Rank without gaps ~ Boşluksuz sıra
        ntile(buckets @ntilebuckets) : int -- Number of the bucket the row falls into ~ Satırın düştüğü dilimin numarası
        lag(value any, [offset int], [default any]) : any -- Value from a previous row ~ Önceki satırdaki değer
        lead(value any, [offset int], [default any]) : any -- Value from a following row ~ Sonraki satırdaki değer
        first_value(value any) : any -- First value in the window ~ Penceredeki ilk değer
        last_value(value any) : any -- Last value in the window ~ Penceredeki son değer
        nth_value(value any, n int) : any -- The n-th value in the window ~ Penceredeki n. değer
        percent_rank() : double precision -- Relative rank, 0 to 1 ~ Göreli sıra, 0 ile 1 arası
        cume_dist() : double precision -- Cumulative distribution, 0 to 1 ~ Kümülatif dağılım, 0 ile 1 arası

        # ---- Arrays ----
        array_length(array any[], dimension int) : int -- Length of an array dimension, 1 for a plain array ~ Dizi boyutunun uzunluğu, düz dizi için 1
        cardinality(array any[]) : int -- Number of elements ~ Öğe sayısı
        array_to_string(array any[], delimiter text, [null_string text]) : text -- Joins the elements into a text ~ Öğeleri metinde birleştirir
        array_append(array any[], element any) : any[] -- Adds an element at the end ~ Sona öğe ekler
        array_remove(array any[], element any) : any[] -- Removes every element equal to the value ~ Değere eşit her öğeyi siler
        array_position(array any[], element any) : int -- Position of the first equal element ~ İlk eşit öğenin konumu
        unnest(array any[]) : setof any -- A row per element ~ Her öğe için bir satır

        # ---- JSON ----
        to_json(value any) : json -- A value as JSON ~ Değeri JSON yapar
        to_jsonb(value any) : jsonb -- A value as jsonb ~ Değeri jsonb yapar
        json_build_object(key text, value any, ...) : json -- A JSON object from keys and values ~ Anahtar ve değerlerden JSON nesnesi
        jsonb_build_object(key text, value any, ...) : jsonb -- A jsonb object from keys and values ~ Anahtar ve değerlerden jsonb nesnesi
        jsonb_build_array(value any, ...) : jsonb -- A jsonb array ~ jsonb dizisi
        json_agg(value any) : json -- The values of a group as a JSON array ~ Grubun değerlerini JSON dizisi yapar
        jsonb_agg(value any) : jsonb -- The values of a group as a jsonb array ~ Grubun değerlerini jsonb dizisi yapar
        jsonb_object_agg(key text, value any) : jsonb -- Keys and values of a group as a jsonb object ~ Grubun anahtar ve değerlerini jsonb nesnesi yapar
        jsonb_extract_path(from_json jsonb, path text, ...) : jsonb -- The value at a path of keys, like the #> operator ~ Anahtar yolundaki değer, #> işleci gibi
        jsonb_extract_path_text(from_json jsonb, path text, ...) : text -- The value at a path of keys as text, like #>> ~ Anahtar yolundaki değer metin olarak, #>> gibi
        jsonb_set(target jsonb, path text[], new_value jsonb, [create_if_missing boolean]) : jsonb -- Sets a value at a path, written '{a,b}' ~ Bir yoldaki değeri değiştirir, yol '{a,b}' yazılır
        jsonb_array_elements(from_json jsonb) : setof jsonb -- A row per array element ~ Her dizi öğesi için bir satır
        jsonb_array_elements_text(from_json jsonb) : setof text -- A row per array element, as text ~ Her dizi öğesi için metin satır
        jsonb_each(from_json jsonb) : setof record -- A key and value row per property ~ Her özellik için anahtar ve değer satırı
        jsonb_object_keys(from_json jsonb) : setof text -- The keys of an object ~ Nesnenin anahtarları
        jsonb_array_length(from_json jsonb) : int -- Number of array elements ~ Dizi öğe sayısı
        jsonb_typeof(from_json jsonb) : text -- object, array, string, number, boolean or null ~ object, array, string, number, boolean ya da null
        jsonb_strip_nulls(from_json jsonb) : jsonb -- Removes properties whose value is null ~ Değeri null olan özellikleri siler
        jsonb_pretty(from_json jsonb) : text -- Indented JSON text ~ Girintili JSON metni
        jsonb_path_query(target jsonb, path @jsonpath) : setof jsonb -- Items a JSON path finds ~ JSON yolunun bulduğu öğeler
        jsonb_path_exists(target jsonb, path @jsonpath) : boolean -- Whether a JSON path finds anything ~ JSON yolu bir şey buluyor mu

        # ---- Sequences ----
        nextval(sequence regclass) : bigint -- Advances a sequence and returns the new value ~ Diziyi ilerletip yeni değeri verir
        currval(sequence regclass) : bigint -- Last value nextval gave in this session ~ Bu oturumda nextval ile alınan son değer
        setval(sequence regclass, value bigint, [is_called boolean]) : bigint -- Sets the value of a sequence ~ Dizinin değerini ayarlar
        lastval() : bigint -- Last value nextval gave in this session, of any sequence ~ Bu oturumda herhangi bir diziden alınan son değer

        # ---- System ----
        current_setting(setting_name @setting, [missing_ok boolean]) : text -- The value of a setting ~ Bir ayarın değeri
        set_config(setting_name @setting, new_value text, is_local boolean) : text -- Changes a setting, for the transaction when is_local ~ Bir ayarı değiştirir, is_local ise işlem boyunca
        current_database() : name -- Name of the current database ~ Geçerli veritabanının adı
        current_schema() : name -- First schema of the search path ~ Arama yolunun ilk şeması
        current_schemas(include_implicit boolean) : name[] -- The schemas of the search path ~ Arama yolundaki şemalar
        version() : text -- Server version text ~ Sunucu sürüm metni
        pg_backend_pid() : int -- Process id of this session ~ Bu oturumun süreç kimliği
        pg_cancel_backend(pid int) : boolean -- Cancels the query of a session ~ Bir oturumun sorgusunu iptal eder
        pg_terminate_backend(pid int, [timeout bigint]) : boolean -- Ends a session ~ Bir oturumu sonlandırır
        pg_sleep(seconds double precision) : void -- Waits a number of seconds ~ Belirtilen saniye bekler
        pg_size_pretty(size bigint) : text -- A byte count as kB, MB, GB ~ Bayt sayısını kB, MB, GB olarak yazar
        pg_total_relation_size(relation regclass) : bigint -- Size of a table with its indexes and TOAST ~ İndeksleri ve TOAST ile tablonun boyutu
        pg_relation_size(relation regclass, [fork text]) : bigint -- Size of the table or index data alone ~ Yalnız tablo ya da indeks verisinin boyutu
        pg_table_size(relation regclass) : bigint -- Size of a table without indexes ~ İndeksler hariç tablonun boyutu
        pg_indexes_size(relation regclass) : bigint -- Size of the indexes of a table ~ Tablonun indekslerinin boyutu
        pg_database_size(name name) : bigint -- Size of a database ~ Veritabanının boyutu
        pg_column_size(value any) : int -- Bytes a value takes when stored ~ Değerin saklanınca kapladığı bayt
        pg_get_functiondef(func oid) : text -- CREATE statement of a function ~ Fonksiyonun CREATE komutu
        has_table_privilege(table text, privilege @privilege) : boolean -- Whether the current user has a privilege on a table ~ Geçerli kullanıcının tabloda yetkisi var mı
        inet_client_addr() : inet -- Address of the client ~ İstemcinin adresi
        """;
}
