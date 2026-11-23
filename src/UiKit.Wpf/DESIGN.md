# UiKit — Tasarım ve UX Rehberi

Veri yoğun masaüstü uygulamaları (veri görüntüleyici, veritabanı istemcisi vb.) için WPF tasarım dili.
İlk olarak VisualData'da kullanıldı; uygulamadan bağımsızdır. Bu belge kural kaynağıdır: yeni ekranlar buradaki token ve kalıplarla kurulur.

Görsel referans: `samples/UiKit.Demo` (gezgin ağacı + sekmeler + sonuç grid'i + bileşen vitrini).

---

## 1. İlkeler

1. **Veri önde, arayüz geride.** Krom (araç çubuğu, paneller) sakin ve düşük kontrastlı; vurgu rengi yalnızca etkileşim, seçim ve aktif durum için.
2. **Tek vurgu rengi.** Mavi (`Accent`). Başarı/uyarı/hata renkleri yalnızca anlam taşıdığında kullanılır.
3. **Yüzey katmanları.** `Window` (en alt) → `Surface` kartlar → `Card` açılır paneller. Ayrım gölgeyle değil, ince 1px kenarlık ve hafif ton farkıyla yapılır (gölge sadece açılır panellerde).
4. **Arayüz asla donmaz.** Yükleme, sorgu, dışa aktarma arka planda çalışır; ilerleme ve **İptal** her zaman görünür.
5. **Klavye birinci sınıf.** Her ana işlemin kısayolu vardır ve araç ipucunda yazar.
6. **Yoğun ama ferah.** 13px gövde metni, 28px satır, cömert iç boşluk; tablolar okunur kalır.

---

## 2. Renk token'ları

Her renk `Color.X` ve `Brush.X` olarak iki temada **aynı anahtarla** tanımlıdır (`Themes/Dark.xaml`, `Themes/Light.xaml`).
XAML'de her zaman `{DynamicResource Brush.X}` kullanın (tema çalışma anında değişir). Kodda sabit renk yazmayın.

| Token | Koyu | Açık | Kullanım |
|---|---|---|---|
| Window | `#15171C` | `#F3F4F7` | Pencere zemini, araç çubuğu, durum çubuğu |
| Surface | `#1B1E25` | `#FFFFFF` | Kartlar, grid zemini, paneller |
| SurfaceAlt | `#1F222A` | `#F8F9FB` | Grid'de çift satırlar (zebra) |
| Header | `#22262F` | `#F4F6F9` | Grid başlığı, satır numarası sütunu |
| Card | `#20242C` | `#FFFFFF` | Açılır paneller, menüler, ipuçları |
| Input | `#1F232A` | `#FFFFFF` | Metin kutusu, ComboBox, arama |
| Chip | `#252A34` | `#E8EEFB` | Filtre çipleri, rozetler |
| Overlay | `#C0101216` | `#C8F3F4F7` | Meşgul katmanı (yarı saydam) |
| Border | `#2C313B` | `#E2E5EB` | Kart kenarları, ayırıcılar |
| BorderStrong | `#3A404C` | `#CDD2DB` | Giriş kenarları, açılır panel kenarı |
| GridLine | `#252932` | `#EEF0F3` | Grid hücre çizgileri |
| Text | `#E7E9EE` | `#1C2128` | Birincil metin |
| TextSecondary | `#AEB5C2` | `#4B5563` | Başlıklar, ikincil metin, ikon butonları |
| TextMuted | `#747C8B` | `#8A93A3` | Açıklama, yer tutucu, NULL, satır numarası |
| Accent | `#5B8CFF` | `#2F6FEB` | Birincil buton, odak, aktif sekme/filtre, sıralama oku |
| AccentHover | `#7AA2FF` | `#1F5DD8` | Vurgu üzerinde hover |
| AccentText | `#FFFFFF` | `#FFFFFF` | Vurgu zeminindeki metin |
| AccentSoft | `#265B8CFF` | `#1A2F6FEB` | Seçili ağaç öğesi, açık toggle, aktif filtre ikonu zemini |
| Selection | `#2E5B8CFF` | `#242F6FEB` | Grid hücre/satır seçimi |
| Hover / Pressed | `#10FFFFFF` / `#1CFFFFFF` | `#0A000000` / `#14000000` | Üzerine gelme / basma katmanı |
| Success / Warning / Danger / Info | `#3FB27F` / `#E8B04B` / `#F06A6A` / `#5FB3E8` | `#1F9D63` / `#B7791F` / `#D64545` / `#1F7FC2` | Anlamsal durum |
| TrueValue | `#5FC79A` | `#1F9D63` | Grid'de `true` değerleri (`false` = TextMuted) |
| ScrollThumb(+Hover) | `#464C58` | `#C3C8D1` | İnce kaydırma çubukları |

**SQL / kod sözdizimi** (editör için): `Brush.Syntax.Keyword` (mavi), `.String` (yeşil), `.Number` (turuncu), `.Comment` (soluk, *italik*), `.Function` (mor), `.Type` (camgöbeği), `.Operator`, `.Identifier`, `.Error`. AvalonEdit vb. kullanılıyorsa vurgulama tanımı bu fırçalardan beslenmeli.

---

## 3. Tipografi

| Rol | Font | Boyut | Ağırlık | Token / stil |
|---|---|---|---|---|
| Arayüz | Segoe UI Variable Text → Segoe UI | 13 | Normal | `Font.UI`, `FontSize.Body` |
| Grid hücre | Segoe UI | 13 | Normal | (VirtualGrid içinde) |
| Grid başlık | Segoe UI | 12.5 | SemiBold | (VirtualGrid içinde) |
| Bölüm etiketi | — | 11 | SemiBold, BÜYÜK HARF, TextMuted | `Text.Section` |
| Açıklama | — | 11.5 | TextMuted | `Text.Caption` |
| Alt başlık / kart başlığı | — | 15 (panelde 13.5) | SemiBold | `Text.Subtitle` |
| Boş durum başlığı | — | 22 | SemiBold | `Text.Title` |
| Durum çubuğu | — | 12 | TextSecondary | `Text.Status` (boşsa gizlenir) |
| Kod / SQL | Cascadia Mono → Consolas | 12.5 | Normal | — |
| İkon | Segoe Fluent Icons → Segoe MDL2 Assets | 13–15 | — | `Font.Icons`, `Text.Icon` |

Pencerede `TextOptions.TextFormattingMode="Display"` ve `UseLayoutRounding="True"` açık olmalı (keskin metin).
Sayılar sağa, metin ve tarihler sola hizalanır.

---

## 4. Ölçüler

| Öğe | Değer |
|---|---|
| Köşe yarıçapı | kontrol 6 · arama 8 · kart 10 · çip 13 · açılır panel 10 (`Radius.*`) |
| Araç çubuğu | iç boşluk 12,9 · buton yüksekliği 34 · alt kenarlık 1px |
| Sayfa kenar boşluğu | 12,10,12,0 (`Space.Page`); kartlar arası 10–12 |
| Kart iç boşluğu | 14 (`Space.Card`); kart başlığı 14,10,8,10 |
| Durum çubuğu | 32 yükseklik, yatay 14 |
| Grid | satır 28 · başlık 36 · hücre iç boşluk 10 · min sütun 40 · otomatik genişlik 64–360 |
| Kaydırma çubuğu | 12 kalınlık, yuvarlak 8px başparmak, ok tuşu yok |
| Açılır panel | gölge Blur 22 / Depth 4 / %30; sağ-alt 14px boşluk (`Card.Popup`) |
| Çip | yükseklik ~26, kapatma butonu 20×20 yuvarlak |

---

## 5. Bileşenler (stil anahtarları)

| Bileşen | Kullanım |
|---|---|
| `Btn.Primary` | Ekrandaki tek ana eylem (Uygula, Çalıştır, Dosya aç) |
| `Button` (varsayılan = `Btn.Secondary`) | İkincil eylem (İptal, Yapıştır) |
| `Btn.Toolbar` | Araç çubuğu: ikon + kısa metin. `ToggleButton` ile açık/kapalı durum vurgulu görünür |
| `Btn.Icon` | Yalnız ikon (34×34; panel başlığında 28×28, çipte 20×20) |
| `Btn.Link` | Metin bağlantısı (Tümünü temizle, Filtreyi temizle) |
| `TextBox` | `c:Ui.Placeholder` ile yer tutucu; odakta vurgu kenarı |
| `TextBox.Bare` | Kenarsız (özel kap içinde) |
| `c:SearchBox` | İkon + yer tutucu + temizle butonu, Esc temizler |
| `ComboBox`, `CheckBox`, `ProgressBar` | Temalı; ProgressBar 4px, belirsiz modda kayan çubuk |
| `TabControl` / `TabItem` | Belge sekmeleri, seçili sekmede 2px vurgu alt çizgisi, `c:Ui.Icon` destekli |
| `TreeView` / `TreeViewItem` | Gezgin; chevron genişletici, `c:Ui.Icon` ikonları, seçili = AccentSoft |
| `GridSplitter` | Görünmez tutma alanı, hover/sürüklemede 2px vurgu çizgisi |
| `ContextMenu` / `MenuItem` | Yuvarlak kart, `c:Ui.Icon` + kısayol metni |
| `ToolTip` | Kart görünümlü, en fazla 560px, metni sarar |
| `Toolbar`, `Card.Surface`, `Card.Header`, `Card.Popup`, `Chip`, `Badge`, `Separator.Vertical` | Kabuk yapı taşları (Border stilleri) |
| `c:VirtualGrid` | Milyonlarca satırlık sonuç tablosu (aşağıda) |

İkonlar: `c:Ui.Icon="&#xE8E5;"` veya kodda `Ui.SetIcon(x, Icons.OpenFile)`. Kod noktaları `Controls/Icons.cs` içinde (Database, Table, Column, Key, Play, Stop, Filter…).

---

## 6. VirtualGrid (sonuç tablosu)

Hücre başına WPF elemanı oluşturmaz; görünen hücreleri `GlyphRun` ile çizer. 1M+ satırda akıcıdır.

```csharp
public interface IGridDataSource
{
    object SchemaKey { get; }   // aynı kaldıkça sütun genişlikleri korunur (filtre/sıralama sonrası)
    int RowCount { get; }
    int ColumnCount { get; }
    string GetColumnName(int column);
    GridColumnKind GetColumnKind(int column);   // Text, Integer, Number, Boolean, DateTime
    string GetText(int row, int column);         // UI thread'de, hızlı olmalı (I/O yok)
    bool IsNull(int row, int column);
}
```

- **Grid sıralamaz/filtrelemez.** Olayları dinleyip arka planda sorgulayın, sonuçta yeni `Source` atayın.
  `SortRequested(col, shift)`, `SortDirectionRequested`, `FilterRequested(col, headerRect)`, `FilterByValueRequested`, `HideColumnRequested`, `CopyRequested`, `SelectionChanged`, `CurrentCellChanged`.
- Göstergeler: `Sorts` (oklar + çoklu sıra numarası), `FilteredColumns` (vurgulu huni).
- Seçenekler: `NullText="NULL"` (veritabanı aracı için önerilir, soluk renkte), `ShowFilterButtons`, `ShowRowNumbers`, `RowHeight`, `VisibleColumns`.
- Görsel kurallar: zebra satırlar, satır hover, yarı saydam seçim, odaklıyken 2px vurgulu yuvarlak geçerli hücre, başlıkta tür etiketi (`Aa`, `#`, `1.2`, takvim, onay), sayılar sağa yaslı, `true` yeşil / `false` soluk, kırpılan metinde 650 ms sonra tam metin ipucu.
- Etkileşim: başlık tık = sırala (Shift = ekle), huni = filtre paneli, kenar sürükle = genişlik, kenara çift tık = sığdır, satır numarası = satır seçimi, sol üst köşe = tümünü seç, sağ tık menüleri, Ctrl+C / Ctrl+Shift+C / Ctrl+A, ok/PgUp/PgDn/Home/End (+Shift).
- Metinler `GridStrings` üzerinden yerelleştirilebilir.
- Büyük sonuçlarda `GetText` için kaynakta veriyi sütun dizilerinde tutun; sayfalı yüklemede henüz gelmemiş satırlar için boş metin döndürüp veri gelince `InvalidateVisual()` çağırın.

---

## 7. Ekran kalıpları

**Kabuk (yukarıdan aşağı):** Araç çubuğu (logo · eylemler · ayırıcı · panel toggle'ları · sağda arama + tema) → isteğe bağlı aktif filtre çipleri → içerik (kartlar, 12px kenar) → durum çubuğu.

**Veritabanı aracı yerleşimi (demo ile aynı):** solda `Card.Surface` içinde Gezgin (`Card.Header` + `TreeView`, ~270px) · `GridSplitter` (10px) · sağda `TabControl` (sorgu sekmeleri; her sekmede editör + sonuç grid'i) · altta durum çubuğu (satır sayısı · süre · bağlantı/şema).

**Boş durum:** ortalanmış; 56–76px yuvarlak `AccentSoft` kare içinde vurgu ikonu → `Text.Title`/`Text.Subtitle` → TextSecondary açıklama (en fazla 2 satır) → birincil + ikincil buton → kısayol ipucu `Text.Caption`.

**Meşgul durumu:** içerik alanını `Overlay` ile örten katman + ortada `Card.Popup` (başlık, 4px ilerleme, ayrıntı metni, **İptal**). Kısa işlemlerde (<300 ms) katman yerine durum çubuğunda küçük belirsiz ilerleme.

**Filtre / ayar açılır paneli:** `Popup` + `Card.Popup`, ~330px; üstte başlık + tür rozeti, `Text.Section` başlıklı bölümler, altta solda `Btn.Link` (Temizle), sağda İptal + `Btn.Primary` (Uygula). Enter uygular, Esc kapatır, hata metni panel içinde `Brush.Danger` ile.

**Detay paneli:** sağda 330px kart; alan adı (TextMuted, 11.5) + değer (seçilebilir salt okunur `TextBox.Bare`) + sağda tür.

---

## 8. UX kuralları

- Uzun işler `Task.Run` + `CancellationToken`; yeni istek eskisini iptal eder.
- Yazarak arama 200–250 ms gecikmeyle (debounce) tetiklenir.
- Hata mesajları yerinde (panelde/durum çubuğunda); modal ileti kutusu yalnız işlem tamamen başarısız olduğunda.
- Sayı/tarih biçimi kullanıcının kültürüyle; dışa aktarma değişmez (invariant) biçimle.
- Metinler Türkçe, kısa, eylem odaklı ("Dosya aç", "Uygula", "Tümünü temizle"); butonlarda nokta yok, menüde diyalog açanlar "…" ile biter.
- Tema Windows ayarını izler, kullanıcı değiştirirse kaydedilir; başlık çubuğu temaya uyar (`ThemeManager.ApplyTitleBar`).
- Standart kısayollar: Ctrl+O aç · Ctrl+S kaydet/dışa aktar · Ctrl+F ara · Ctrl+C/Ctrl+Shift+C kopyala · Ctrl+A tümü · Esc kapat/temizle · Ctrl+Shift+L filtreleri temizle · (SQL) Ctrl+Enter çalıştır.

---

## 9. Bir projeye ekleme (ör. JetQueries)

1. `src/UiKit.Wpf` projesini çözüme ekleyin (veya kopyalayın) ve WPF uygulamasından referans verin.
2. `App.xaml`:
   ```xml
   <ResourceDictionary.MergedDictionaries>
       <ResourceDictionary Source="pack://application:,,,/UiKit.Wpf;component/Themes/Dark.xaml" />
       <ResourceDictionary Source="pack://application:,,,/UiKit.Wpf;component/Themes/Controls.xaml" />
   </ResourceDictionary.MergedDictionaries>
   ```
3. `App.OnStartup`: `ThemeManager.Initialize("JetQueries");` — her pencerede `SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);`
4. Pencere kökü:
   ```xml
   xmlns:c="clr-namespace:UiKit.Controls;assembly=UiKit.Wpf"
   Background="{DynamicResource Brush.Window}" Foreground="{DynamicResource Brush.Text}"
   FontFamily="{StaticResource Font.UI}" FontSize="{StaticResource FontSize.Body}"
   UseLayoutRounding="True" TextOptions.TextFormattingMode="Display"
   ```
5. Sorgu sonucu için `IGridDataSource` uygulayın (sütun dizileri veya DataReader'dan doldurulan tampon), `<c:VirtualGrid Source="{Binding Result}" NullText="NULL" />`.
6. Yeni ekranlarda yalnız bu belgedeki token ve stilleri kullanın; yeni renk gerekiyorsa iki tema dosyasına aynı anahtarla ekleyin.
