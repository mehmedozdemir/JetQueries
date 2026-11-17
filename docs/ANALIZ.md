# JetQueries — Ürün Analizi

Kaynak: "Veritabanı Geliştirme ve Sorgulama Aracı — Ürün Analizi" dokümanı (17 Kasım 2026). Onaylanan kararlar en sondadır.

## 1. Yönetici özeti ve vizyon

JetQueries; SQL Server, Oracle, PostgreSQL, SQLite ve Redis'e tek arayüzden bağlanan, .NET 10 + Avalonia ile yazılmış, eklenti tabanlı, çapraz platform bir veritabanı IDE'sidir. Hedef, DataGrip'in editör zekâsını DBeaver'ın genişliği ve açıklığıyla birleştirip ikisinin zayıf noktası olan hız ve sadeliği kazanmaktır.

**Konumlandırma:** "DataGrip kadar akıllı, DBeaver kadar kapsamlı, ikisinden de hızlı ve sade."

Üç temel ilke:

1. **Arayüz asla donmaz.** Her I/O, parse ve metadata işlemi arka planda çalışır; her uzun işlem iptal edilebilir.
2. **Çekirdek küçük, gerisi eklenti.** Veritabanı sürücüleri dahil her yetenek yayımlanmış bir eklenti sözleşmesi üzerinden gelir. Kendi özelliklerimiz de aynı API'yi kullanır.
3. **Editör birinci sınıf vatandaştır.** IntelliSense, gezinme ve refactoring kalitesi ürünün kalbidir.

## 2. Hedef kullanıcılar

| Persona | Tipik günü | En çok ihtiyaç duyduğu | Öncelik |
| --- | --- | --- | --- |
| Veritabanı geliştirici | Prosedür, view, tablo yazar; çok sayıda şema arasında gezer | Akıllı IntelliSense, Ctrl+Tık ile kaynak koda gitme, DDL üretme, şema karşılaştırma | MVP |
| Uygulama geliştirici / analist | Günde onlarca ad hoc sorgu çeker, sonucu Excel'e atar | Hızlı bağlantı, sorgu geçmişi, kayıtlı sorgular, hızlı export | MVP |
| DBA | Oturumları, kilitleri, yavaş sorguları, yetkileri izler | Execution plan, oturum/kilit izleme, kullanıcı-rol yönetimi | Faz 3 |
| Raporlayıcı | Aynı sorguları parametreyle düzenli çalıştırır | Parametreli kayıtlı sorgu, zamanlanmış export, basit grafik | Faz 3–4 |

Ortak acı noktaları: Java tabanlı araçların ağır açılışı ve bellek tüketimi, DBeaver'da Oracle paket/prosedür deneyiminin zayıflığı, çok sayıda müşteri ortamı ve VPN arasında bağlantı karmaşası, SSMS'in yalnızca Windows ve yalnızca SQL Server olması.

## 3. Rakip analizi

| Araç | Alacağımız en iyi özellikler | Kaçınacağımız zayıflıklar |
| --- | --- | --- |
| DataGrip | Bağlama duyarlı tamamlama (JOIN koşulu, alias'a göre kolon), Ctrl+Tık, Find usages, güvenli rename, live templates, lokal sorgu geçmişi, çoklu sonuç sekmesi, introspection seviyeleri | Ağır indeksleme, ticari lisans, JVM bellek tüketimi |
| DBeaver | Geniş sürücü yelpazesi, ER diyagramı, veri editörü, veri transferi ve export, bağlantı klasörleri ve ortam rengi, SSH tüneli, read-only mod | Kalabalık menüler, yavaş UI, ortalama IntelliSense |
| TablePlus | Anlık açılış, sade arayüz, değişiklikleri önizleyip commit etme, Redis desteği | Sınırlı geliştirici araçları, zayıf eklenti ekosistemi |
| SSMS | Görsel execution plan, Activity Monitor, şablonlar, ScriptDom ile doğru T-SQL parse | Yalnızca Windows ve SQL Server |
| Azure Data Studio | Notebook fikri, eklenti pazarı | Emekliye ayrıldı; boşta kalan kullanıcı kitlesi bir fırsat |
| RedisInsight | Anahtar ağacı (`:` ile gruplama), tip bazlı görüntüleyiciler, yavaş log, bellek analizi | — |

Fark yaratacak üç şey: (1) native .NET hızı ve düşük bellek, (2) Oracle paket/PL-SQL deneyiminde DBeaver'dan belirgin şekilde iyi olmak, (3) .NET ekosistemine doğal entegrasyon (EF Core, C# sınıf üretimi, connection string yönetimi).

## 4. Fonksiyonel kapsam

| # | Modül | Ana özellikler | Faz |
| --- | --- | --- | --- |
| 1 | Bağlantı yöneticisi | Klasör/etiket, ortam rengi (Dev/Test/UAT/Prod), SSH tüneli, read-only ve tehlikeli komutta onay, bağlantı testi, DBeaver/DataGrip/SSMS'ten içe aktarma | 1 |
| 2 | Obje gezgini | Lazy ağaç, tip filtresi, "Go to object" (Ctrl+Shift+N), favoriler, sağ tık ile DDL / SELECT / INSERT üretme | 1 |
| 3 | SQL editörü | Renklendirme, IntelliSense, Ctrl+Tık, çoklu imleç, formatlama, snippet, imleçteki ifadeyi çalıştırma | 1 |
| 4 | Sonuç grid'i | Sanallaştırılmış, milyon satırda akıcı; çoklu sonuç sekmesi, filtre/sıralama, JSON/XML/BLOB/resim görüntüleyici, seçimden toplam/ortalama, formatlı kopyalama | 1 |
| 5 | Veri editörü | Hücre düzenleme, bekleyen değişiklikleri önizleme, toplu commit/rollback, üretilen DML'i gösterme | 2 |
| 6 | Sorgu kütüphanesi | Otomatik geçmiş (bağlantı, süre, satır sayısı), kayıtlı sorgular, klasör/etiket, parametreli sorgular, tam metin arama | 1–2 |
| 7 | Export / Import | CSV, Excel, JSON, XML, SQL INSERT, Markdown; stream ederek yazma; CSV/Excel'den import, kolon eşleme | 2 |
| 8 | Şema araçları | Tablo/index/view tasarımcısı, şema karşılaştırma ve fark scripti, ER diyagramı | 2–3 |
| 9 | Execution plan | SQL Server XML plan, Oracle `EXPLAIN PLAN`/`DBMS_XPLAN`, PostgreSQL `EXPLAIN (FORMAT JSON)`; maliyetli düğüm vurgusu | 3 |
| 10 | DBA paneli | Aktif oturumlar, blocking zinciri, uzun sorgular, kullanıcı-rol-yetki, tablo/index boyutları | 3 |
| 11 | Raporlama | Parametreli rapor, sonuçtan grafik, zamanlanmış çalıştırma ve export | 4 |
| 12 | Redis gezgini | Anahtar ağacı, tip bazlı görüntüleyici, TTL düzenleme, CLI konsolu, `SCAN` ile tarama | 2 |

MVP'de bilerek dışarıda: ER diyagramı, şema karşılaştırma, DBA paneli, notebook, AI.

## 5. SQL editörü

Editör AvaloniaEdit üzerine kurulur; zekâsı editörden bağımsız bir **dil servisi** (`ISqlLanguageService`) katmanından gelir. Her lehçe kendi parser'ını eklenti olarak getirir.

Dil servisi işlemleri: `Tokenize` (satır bazlı, artımlı), `Parse` (arka planda, 150–250 ms debounce), `GetCompletions`, `GetDefinition` (Ctrl+Tık), `GetHover`, `GetSignatureHelp`, `Format`, `GetFoldingRanges`, `FindReferences`.

| Lehçe | Parser | Not |
| --- | --- | --- |
| T-SQL | Microsoft.SqlServer.TransactSql.ScriptDom | Resmi parser, MIT |
| PL/SQL | Elle yazılmış yapısal ayrıştırıcı + token tabanlı tamamlama bağlamı (ADR 0003) | Faz 0 ölçümünde ANTLR4 grameri hız ve bellek bütçesini aşmıştır; sözdizimi hataları veritabanı derlemesinden (`USER_ERRORS`) gelir |
| PostgreSQL | ANTLR4 veya libpg_query bağlayıcısı | libpg_query native bağımlılık getirir |
| SQLite | ANTLR4 | Basit |
| Redis | Komut tablosu (`COMMAND DOCS`) | Parser yerine komut + argüman şeması |

ANTLR parser'ları yarım SQL'de error recovery için ayrıca ayarlanmalı.

**IntelliSense:** `FROM`/`JOIN` sonrası tablolar, alias + `.` ile kolonlar, FK'den `JOIN … ON` önerisi, `SELECT *` genişletme, CTE/alt sorgu/geçici tablo kolonları, Oracle paket üyeleri ve sequence, bulanık eşleştirme, son kullanılana göre sıralama, diske yazılan metadata önbelleği.

**Gezinme:** Ctrl+Tık / F12 ile tablo detay sekmesi veya kaynak kod; Ctrl basılıyken alt çizili link + özet tooltip; Alt+F7 kullanımları bul; Ctrl+Shift+N objeye git; Alt+←/→ geçmiş.

**Çalıştırma:** Ctrl+Enter imleçteki ifade / seçim, F5 tüm script; her çalıştırma iptal edilebilir; Prod'da tehlikeli komut onayı; manuel/otomatik commit, açık transaction uyarısı; parametre (`:p`, `@p`, `$1`) giriş penceresi.

## 6. Veritabanı sağlayıcıları

| Veritabanı | Sürücü | Metadata | Özel dikkat |
| --- | --- | --- | --- |
| SQL Server | Microsoft.Data.SqlClient | `sys.*` | Windows/Entra ID auth, çoklu sonuç kümesi, `GO`, `PRINT`/`RAISERROR` |
| Oracle | Oracle.ManagedDataAccess.Core | `ALL_*` / `DBA_*` | TNS/EZConnect/Wallet, paket spec/body, `DBMS_OUTPUT`, `REF CURSOR`, tırnaklı isimler |
| PostgreSQL | Npgsql | `pg_catalog`, `information_schema` | `search_path`, `RAISE NOTICE`, JSONB/array, `COPY` |
| SQLite | Microsoft.Data.Sqlite | `sqlite_master`, `PRAGMA` | Dosya kilidi, `ATTACH` |
| Redis | StackExchange.Redis | `INFO`, `SCAN`, `TYPE`, `MEMORY USAGE` | Ayrı gezgin ve konsol; `KEYS *` yasak; Cluster/Sentinel/TLS |

Ortak soyutlama: `IDatabaseProvider` (bağlantı fabrikası + yetenek bayrakları), `IMetadataProvider` (seviyeli yükleme), `IQueryExecutor` (`IAsyncEnumerable` satır stream'i, mesaj kanalı, iptal), isteğe bağlı `IDdlGenerator`, `IPlanProvider`, `ISessionMonitor`; Redis için ayrı `IKeyValueProvider`.

## 7. Mimari

Beş katman: Sunum (Avalonia + MVVM, UI thread) → Uygulama çekirdeği (iş kuyruğu, metadata önbelleği, oturum, geçmiş/ayarlar) → `JetQueries.Sdk` (sözleşmeler, eklenti host) ← Eklentiler (sağlayıcılar, araçlar) → Altyapı (kimlik deposu, SSH, Velopack, telemetri).

Asenkron model:

- Her işlem bir `Job`: öncelik, ilerleme, iptal; `System.Threading.Channels` tabanlı.
- Sonuçlar `IAsyncEnumerable` ile akar; grid 500–1000 satırlık partilerle, en fazla 16 ms'de bir toplu güncellenir.
- Büyük sonuçlarda ilk sayfa bellekte, gerisi istendikçe; eşik aşılırsa geçici diske.
- Ayrı metadata bağlantısı: uzun sorgu IntelliSense'i kilitlemez.
- Parse/tamamlama ayrı görevde, debounce + önceki işi iptal.
- UI thread'de senkron I/O analyzer kuralıyla build'i kırar.

## 8. Eklenti modeli

- Eklenti = `JetQueries.Sdk`'ya karşı yazılmış .NET kütüphanesi, kendi collectible `AssemblyLoadContext`'inde.
- Paket: `.jqext` zip (assembly'ler, ikonlar, yerelleştirme, `extension.json` manifest).
- Manifest: kimlik, SemVer sürüm, uyumlu SDK aralığı, bildirimsel katkı noktaları, aktivasyon olayları (`onConnectionType:oracle`, `onCommand:x`), istenen izinler.
- Katkı noktaları: veritabanı sağlayıcısı, dil servisi, komut + kısayol, menü/bağlam menüsü, araç paneli/belge sekmesi, hücre görüntüleyici, export/import formatı, tema/ikon seti, ayarlar sayfası.
- Hatalar yakalanır, uygulama çökmez; UI thread'de 50 ms'yi aşan eklenti işaretlenir. İleride güvenilmeyen eklentiler için out-of-process seçeneği.
- Dağıtım: önce dosyadan kurulum + JSON katalog feed'i; açık pazar Faz 4. Eklentiler imzalanır.

## 9. Tasarım ve UX

- "Sakin ve yoğun" tasarım dili; referans: JetBrains New UI, Linear, VS Code.
- Kendi token seti (renk, tipografi, 4 px boşluk, radius); açık/koyu tema; Inter + JetBrains Mono/Cascadia Code; tek ikon seti; obje tipi renk kodu; Prod için kırmızı şerit.
- Yerleşim: solda gezgin, ortada belge sekmeleri, altta sonuç/mesaj/plan, sağda araç panelleri; Dock.Avalonia ile kaydedilebilir yerleşim, bölünebilir sekmeler.
- Komut paleti (Ctrl+Shift+P), düzenlenebilir kısayollar, DataGrip/SSMS kısayol profilleri; her uzun işlemde ilerleme + iptal; modal pencere yalnızca tehlikeli işlem onayında.
- Türkçe ve İngilizce arayüz; WCAG AA kontrast; `i/İ` için invariant karşılaştırma.

## 10. Güvenlik ve bağlantı yönetimi

- Parolalar OS kimlik deposunda; isteğe bağlı ana parolalı taşınabilir kasa.
- Bağlantı tanımı ile sır ayrı: tanımlar paylaşılabilir/senkronlanabilir, parolalar makinede kalır.
- Ortam profilleri: "önce X VPN'i açık olmalı" ön koşulu, bağlanmadan önce erişilebilirlik kontrolü ve anlaşılır hata.
- SSH tüneli, SOCKS proxy; Windows Auth, Entra ID, Kerberos, Oracle Wallet.
- Read-only bağlantı, Prod'da tehlikeli komut onayı, oto-commit kapalı varsayılanı.
- Lokal denetim izi; eklentilerin kimlik deposuna erişimi izinle; telemetri opt-in, sorgu metni ve veri asla gönderilmez.

## 11. Sürüm yönetimi ve güncelleme

- Velopack: delta güncelleme, kanallar, arka planda indirme. SemVer + Conventional Commits; sürüm git etiketinden (MinVer); CHANGELOG otomatik.
- Kanallar: Stable (4–6 haftada MINOR), Beta (MINOR'dan 1–2 hafta önce), Nightly (her başarılı main build).
- Akış: açılışta ve 6 saatte bir kontrol → sessiz indirme → "yeniden başlatınca güncellenecek" → açık sekmeler geri yüklenir. Kurumsal ortam için sunucu adresi ayarlanabilir, otomatik güncelleme kapatılabilir.
- CI: etiket push → test → `dotnet publish` (önce win-x64) → imzalama → `vpk pack` → feed'e yükleme.
- Kullanıcı verisi (lokal SQLite) şema sürümü taşır, her sürümde yedek + migration.

## 12. Fonksiyonel olmayan gereksinimler

| Alan | Hedef |
| --- | --- |
| Soğuk açılış | < 1,5 sn (ReadyToRun) |
| Boşta bellek | < 200 MB |
| Tuş → ekranda karakter | < 16 ms |
| IntelliSense listesi | < 100 ms (önbellek sıcak) |
| İlk sonuç satırları | DB ilk satırı döndükten sonra < 100 ms |
| Grid kaydırma | 1 milyon satırda 60 fps |
| UI thread blokajı | 50 ms üzeri loglanır |
| 10.000 tablolu şema metadata | < 5 sn, arka planda |
| Platformlar | Windows 10+ (öncelik), macOS 13+, güncel Ubuntu/Fedora |
| Kararlılık | Çökme < %0,1 oturum; çökmede oturum kurtarma |

Native AOT, dinamik eklenti yüklemeyle uyumsuz; ReadyToRun + tiered PGO kullanılır.

## 13. Yol haritası

| Faz | İçerik | Kapı |
| --- | --- | --- |
| 0 · Temel | Solution iskeleti, CI/CD, imzalama, Velopack ile ilk paket; sanal grid ve PL/SQL parser prototipleri; metadata önbelleği | Grid milyon satırda akıcı, PL/SQL test korpusu parse ediliyor |
| 1 · MVP | Bağlantı yöneticisi, obje gezgini, editör, IntelliSense, Ctrl+Tık, sonuç grid'i, geçmiş; SQL Server ve Oracle derin, PostgreSQL ve SQLite temel | Ekip içi 1.0; günlük iş SSMS ve DBeaver açmadan yapılabiliyor |
| 2 · Verimlilik | Veri editörü, export/import, kayıtlı ve parametreli sorgular, bağlantı ve sorgu senkronu, Redis gezgini, tablo tasarımcısı, SDK preview, beta kanalı | Beta kanalında kararlı sürüm, ilk 3. taraf eklenti çalışıyor |
| 3 · DBA ve şema | Execution plan, oturum/kilit izleme, yetki yönetimi, şema karşılaştırma, ER diyagramı | SDK 1.0 dondurulur |
| 4 · Ekosistem | Raporlama, zamanlanmış export, eklenti kataloğu/pazarı, AI destekli sorgu yardımı, yeni sağlayıcılar | — |

MVP "bitti" tanımı:

- [ ] Gerçek müşteri ortamındaki SQL Server ve Oracle şemalarına VPN üzerinden bağlanılabiliyor
- [ ] IntelliSense alias, JOIN ve Oracle paket üyelerinde doğru öneri veriyor
- [ ] Ctrl+Tık tablo, view, prosedür ve paket gövdesine gidiyor
- [ ] Uzun sorgu çalışırken editör, gezgin ve diğer sekmeler kullanılabiliyor; sorgu iptal edilebiliyor
- [ ] Açılış, bellek ve grid hedefleri CI benchmark'ında geçiyor
- [ ] Uygulama kendini güncelleyebiliyor ve güncellemede açık sekmeler geri geliyor

## 14. Riskler

| Risk | Etki | Azaltma |
| --- | --- | --- |
| Hazır DataGrid milyon satırda yetersiz | Yüksek | Faz 0'da `DrawingContext` ile çizilen sanal grid prototipi; TreeDataGrid lisans durumu ayrıca kontrol |
| PL/SQL parser'ın hatalı kodda toparlanamaması | Yüksek | Gerçek paketlerden test korpusu; token bazlı yedek tamamlama |
| Kapsamın DBeaver kadar büyümesi | Yüksek | Faz kapıları, önceden yazılmış "bitti" tanımları |
| AvaloniaEdit'in 10 MB+ scriptte yavaşlaması | Orta | Görünür alanla sınırlı renklendirme/parse |
| Eklenti API'sinin erken donması | Orta | SDK 1.0'a kadar preview; yerleşik özellikler SDK ile yazılır |
| macOS/Linux imzalama ve paketleme yükü | Orta | CI'da erken kurulum; önce Windows |
| Eski Oracle sürümleri (11g) ile uyum | Düşük–orta | Desteklenen sürüm matrisi |

## 15. Onaylanan kararlar

| Konu | Karar |
| --- | --- |
| Ürün adı | **JetQueries** |
| Lisans / iş modeli | **Açık kaynak** (lisans türü ilk public push öncesi seçilecek; öneri MIT) |
| MVP'de "mükemmel" veritabanları | **SQL Server ve Oracle** |
| Ekip | Önce tek geliştirici (Mehmet), sonra 5, ardından 10 kişi |
| AI özellikleri | **En son** (Faz 4) |
| Senkron | **Evet**: bağlantı tanımları (parolasız) ve kayıtlı SQL sorguları senkronlanacak (Faz 2) |
| SSMS eklentisi | **Evet**: SsmsToolkit'teki Ctrl+Tık ve tablo detayı mantığı JetQueries'e taşınacak |
