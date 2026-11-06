# Ferahla

**Windows diskini neyin doldurduğunu bul, hiçbir şeyi bozmadan temizle.**

[English](README.md)

> **Durum: erken geliştirme.** Ferahla bir sürücüyü tarayıp bulduklarını açıklayabiliyor ve seçtiklerini komut
> satırından geri alınabilir bir karantinaya taşıyabiliyor. Sırada tarayıcı arayüzü var. Henüz sürüm yok: kaynaktan derle.

Çoğu disk temizleyici büyük klasörleri gösterir, gerisi sana kalır. Ferahla her öneri için şunu söyler:

- **ne olduğunu**: önbellek, derleme çıktısı, zaten kurduğun bir programın kurulum dosyası, zaten açtığın bir arşiv…
- **riskini**: *risksiz*, *yeniden üretilir* (geri gelir ama zaman/indirme ister), *bak, karar ver* ya da *kişisel* (bilgi)
- silinirse **neyin bozulacağını** ve **nasıl geri geleceğini**
- **kanıtını**: "zip içeriği klasörde var: 33/33", "Unity projelerinin hiçbiri bu editörü kullanmıyor",
  "`oyunum` ile aynı remote", "içerik 18 noktada eşleşiyor"

Windows'un kendisini, sayfa dosyasını, çalışan bir sanal makinenin dosyalarını ya da Program Files içindeki herhangi
bir şeyi elle silmek için hiçbir zaman önermez.

## Neler bulur

| | Örnekler |
|---|---|
| Önbellekler | Temp, pip, uv, npm, NuGet, Cargo, Gradle, conda, Unity, GPU shader önbellekleri, tarayıcı önbellekleri |
| Derleme çıktıları | `target/`, `node_modules/`, `bin/obj`, Unity `Library/`, CMake, Gradle, Python `.venv`… proje işaretlerinden |
| Kurulum dosyaları | Downloads/Desktop'ta, zaten kurulu uygulamaların kurulum dosyaları (en yenisi kalır) |
| Arşivler | açılmış `x\` klasörünün yanındaki `x.zip` (girdi girdi doğrulanır), hiç açılmamış eski çok parçalı arşivler |
| Kopyalar | içeriği aynı büyük dosyalar (hardlink'ler hariç); proje klasöründeki kopya kalır |
| Eski sürümler | hiçbir projenin kullanmadığı Unity editörleri, eski CLI sürümleri, eski Playwright tarayıcıları, eski Windows SDK'ları |
| Repo kopyaları | aynı git remote'unun ikinci çalışma kopyaları, ParrelSync klonları |
| Oyunlar | Steam, EA, Riot ve Epic oyunları; kendi launcher'larından kaldırmak için |
| Sistem | hazırda bekletme dosyası (`powercfg -h off`), WinSxS (DISM ile, asla elle), WSL diski, taramanın göremediği alan |

Kurallar [`rules/`](rules) altında JSON olarak, İngilizce ve Türkçe metinlerle duruyor.

## Kullanım

```
ferahla scan                 sistem sürücüsünü tara (ya da: ferahla scan D:)
ferahla findings             son taramanın sınıflandırılmış önerileri
ferahla findings --risk safe
ferahla diff                 önceki taramadan beri ne büyüdü, ne küçüldü
ferahla inspect <yol>        bir klasörün boyutu, alt klasörleri ve bulguları
ferahla report --open        tek dosyalık, salt okunur HTML rapor (tarayıcıda)
ferahla scans                kayıtlı taramalar

ferahla quarantine --plan <dosya> --dry-run   rapordaki "Plan dosyasını indir" ile alınan planı kontrol et
ferahla quarantine --plan <dosya>             plandakileri karantinaya taşı
ferahla quarantine --list                     karantinada neler var
ferahla restore <kayıt>                       bir karantina kaydını eski yerine koy
```

Her komut `--json` alır (sürümlü şema, anlamlı exit kodları); böylece betikler ve yapay zekâ ajanları da Ferahla'yı
kullanabilir.

### Karantina

Karantina silme değil, taşımadır: öğeler aynı sürücüdeki bir klasöre taşınır (anında olur). Yer, kalıcı olarak
sildiğinde açılır. O zamana kadar `ferahla restore` her şeyi geri koyar.

- Plan sadece bir seçimdir. Ferahla bir şeyi taşımadan önce o taramanın bulgularını yeniden hesaplar ve bir öğeyi
  sadece hâlâ tam olarak o öğeyi öneriyorsa taşır. Elle değiştirilmiş ya da eskimiş bir plan başka bir şey taşıyamaz.
- Plan ne derse desin Windows, Program Files, sürücü kökleri ve profil/sistem klasörlerinin kendileri taşınmaz.
- Temp'te son 24 saatte değişmiş öğeler yerinde kalır.
- Kullanımda olan bir öğe atlanır ve onu tutan programla birlikte raporlanır (Restart Manager ile); gerisi devam eder.
- Karantina, Ferahla'nın verisinin olduğu sürücüde `%LOCALAPPDATA%\Ferahla\quarantine` altında; diğer sürücülerde
  sadece senin, SYSTEM'in ve Administrators'ın açabildiği gizli `X:\.ferahla\quarantine` klasöründe durur. Sadece sabit
  NTFS/ReFS sürücüler; USB ve ağ sürücülerinde sadece bilgi gösterilir.
- Kalıcı silme bilerek bir komut değil; arayüzde bir buton olacak.

Birkaç milyon dosyalı bir sürücünün tam taraması ilk seferde yaklaşık bir dakika, sonrasında bir dakikadan çok daha kısa
sürer. Taramalar `%LOCALAPPDATA%\Ferahla\scans` altında saklanır.

## Gizlilik

- Telemetri yok, ağ erişimi yok. HTML rapor internetten hiçbir şey yüklemez.
- Taramalar ve raporlar bilgisayarında kalır. **Rapor dosya yollarını içerir**: paylaşmadan önce bak.

## Derleme

Windows 10 1809+ ya da 11 (x64), .NET 10 SDK ve NativeAOT bağlama adımı için Visual Studio 2022'nin
*Desktop development with C++* bileşeni gerekir. `vswhere.exe` `PATH`'te olmalı:

```powershell
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet test --solution Ferahla.slnx
dotnet publish src/Ferahla -c Release -o publish   # publish\ferahla.exe, ~5 MB tek dosya
```

## Yol haritası

1. Aynı raporu canlı gösteren yerel tarayıcı arayüzü: seç, karantinaya al, geri al, kalıcı sil
2. İsteğe bağlı: kendi Claude Code kurulumunla analiz; notlar rapora yazılır
3. Derin kontroller, sistem işlemleri, uygulamalar ve oyunlar sayfaları, geçmiş

## Lisans

[MIT](LICENSE). Üçüncü taraf bildirimleri: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
