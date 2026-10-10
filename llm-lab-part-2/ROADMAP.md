# Yol haritası

Durum: **Faz 1 tamam.** Faz 2 part-2'de kodlandı, kullanıcı testini bekliyor. Değişiklikler kullanıcı kararıyla yapılır; varsayımlar "(varsayım)" ile işaretlidir.

Her fazın mimari diyagramı: [DIAGRAMS.md](DIAGRAMS.md).

## Faz 1: temel (tamam)
Katmanlı `agent-api` (Agent Framework, oturumlar), RAG (hibrit arama, SQL Server `VECTOR`), OpenTelemetry + Aspire Dashboard,
eski `LlmApi` kıyas tabanı, Docker/Compose/kind (şimdilik eski API için). Bkz. [ARCHITECTURE.md](ARCHITECTURE.md).

## Faz 2: ajan yetenekleri ve birlikte çalışabilirlik (part-2'de kodlandı; kullanıcı testini bekliyor)

Durum işaretleri: ✅ kodlandı ve geliştirici denemesinden geçti · ⚠️ kısmen / bilinen sınırı var. Ayrıntı ve ölçümler:
`agent-api/README.md` ("Araçlar ve MCP", "Çoklu ajan").

1. ✅ **Araçlar**: `get_current_time`, `calculate`, `list_documents`, `search_documents`. `Agent:SearchFirst` kaldırıldı
   (kullanıcı kararı): model aramayı kendisi seçiyor (6/6 ölçüldü).
2. ⚠️ **Kod çalıştırma sandbox'ı** (`LlmLab.Mcp.Sandbox`, `run_python`): tek kullanımlık Docker konteyneri, ağ yok, salt okunur
   dosya sistemi, bellek/CPU/süre/süreç sınırı, kök olmayan kullanıcı, stdin ile kod. İzolasyon bayrakları doğrulandı.
   **Eksik:** `python:3.12-slim` imajı yerelde yok (sandbox imaj indirmez; `docker pull` gerekir), bu yüzden kodun gerçekten
   çalıştırılması uçtan uca denenmedi. Riskli işlemlerde kullanıcı onayı faz 3'te.
3. ✅ **MCP**: üç MCP sunucusu (araçlar, belgeler, sandbox), ajan yalnızca MCP istemcisi. Kalan: kimlik doğrulama (faz 3),
   sunucularda OpenTelemetry kurulumu (iz bağlamı HTTP ile taşınıyor), Docker/k8s.
4. ✅ **İki ajanın konuşması**: `POST /dialogs/stream` ve arayüzde Diyalog sekmesi (kayıt tutmaz, en fazla 12 tur).
5. ⚠️ **Koordinatör ve alt ajanlar**: başlat / sonuç al / durdur (`ISubAgentRunner` portu). Süreç içi uygulama; API yeniden
   başlarsa görevler kaybolur (faz 3'te Dapr ile).
6. ⚠️ **A2A**: `LlmLab.AgentHost` kayıt defterindeki ajanları ayrı servis olarak yayınlar (ajan kartı + HTTP+JSON);
   `hosting: remote` ajan oradan çalışır. Paketler önizleme sürümünde; uzak ajanda düşünme/araç/kaynak görünmez; yeni ajan için
   servis yeniden başlatılır; Docker'a ayrı konteyner olarak alınmadı.
7. ✅ **Otomatik değerlendirme hattı** (`evals/`): soru seti + deterministik kontroller + LLM-as-judge + baseline ile regresyon.
   İlk koşu 8/8; yargıç ajanla aynı model (kayırma riski), baseline henüz oluşturulmadı.
8. ✅ **Ajan kayıt defteri**: ajanlar SQL'de veri (rol talimatı, izinli araçlar, devretme, çalışma yeri, etkin/pasif, sürüm);
   arayüzde "çalışsın" anahtarı, düzenleme ve sohbette ajan seçimi. **Eksik:** beceri (skills) paketleri, model/sıcaklık ayarı,
   sürüm geçmişi ve düzenleme yetkisi (faz 3 güvenlik), ajan başına "ön arama" ayarı (gerek gözlenmedi).

Önerilen sıra (izlendi): araçlar → MCP → sandbox → kayıt defteri → iki ajan → koordinatör → A2A; değerlendirme hattı çoklu
ajandan önce kuruldu.
## Faz 3: işletme ve dayanıklılık
1. **Gözlem yığını**: OpenTelemetry Collector + Prometheus (metrik) + Loki (günlük) + Tempo (iz) + Grafana; alarmlar ve SLO'lar.
   (Prometheus yalnızca metrik saklar; günlük ve iz için Loki/Tempo gerekir.)
2. **Dapr ile dayanıklı uzun işler**: kuyruk, yeniden deneme, kaldığı yerden devam, uzun süren alt ajan görevleri ve belge yükleme.
   Dapr iş akışını yönetir, ajan Dapr'ın aktivitesi olarak çalışır (Agent Framework workflow'larıyla karıştırılmaz).
   Kurulumdan önce doğrulanacak: durum deposu seçenekleri (SQL Server desteği), `dapr init` Docker gereksinimi, yan araç (sidecar) maliyeti.
   **Dapr Actors** (kullanıcı önerisi): kimliği olan, durumlu varlıklar için. İş akışı "bir işi baştan sona yönetir", aktör "bir varlığın durumunu ve sırasını tutar".
   Aday kullanımlar (varsayım, kurulumda doğrulanacak):
   - **Konuşma/oturum aktörü** (kimlik = conversationId): aynı sohbete gelen istekler sıraya girer (aktörler tek iş parçacıklı, sıralı çalışır), böylece iki sekme aynı oturumu bozmaz; `AgentSession` durumu aktörde.
   - **Alt ajan görev aktörü** (kimlik = taskId): durum (çalışıyor/bitti/iptal), ilerleme, sonuç; "durdur" ve "durum sor" çağrıları buraya gider. Faz 2'deki alt ajan portunun Dapr uygulaması.
   - Zamanlayıcı/hatırlatıcı (reminder): periyodik kontrol, zaman aşımı, süresi dolan onayların temizlenmesi.
   Dikkat: aktör sıralı çalıştığı için dakikalarca süren bir LLM çağrısı o aktörü kilitler; uzun işi aktörün içinde değil workflow aktivitesinde çalıştırıp aktöre yalnızca durumu yazdırmak daha sağlıklı.
   Aktörler bir aktör destekli durum deposu ister; SQL Server V2 listede aktör destekli görünüyor (doğrulandı), Redis de.
3. Güvenlik ve yönetişim: araç izinleri/insan onayı, bütçe ve zaman sınırları, kimlik doğrulama, kullanıcıya özel belge erişimi,
   denetim kaydı, telemetride kişisel veri maskeleme, daha sıkı sandbox izolasyonu (gVisor/Firecracker).
4. Üretime hazırlık: `agent-api` için Dockerfile ve Kubernetes manifesti, CI hattı, Testcontainers ile entegrasyon testleri.
5. **Redis'in yeri** (kullanıcı önerisi). Redis tek başına doğruluk kaynağı olmaz; hızlı ve uçucu işler içindir:
   - SignalR backplane (Faz 4: birden çok API kopyası arasında hub mesajları).
   - Dapr pub/sub ve (istenirse) Dapr durum deposu. Dapr Workflow için SQL Server da desteklenir, o yüzden zorunlu değil (varsayım: iş akışı durumu SQL'de kalır).
   - Ajan kayıt defteri önbelleği ve değişiklik bildirimi: SQL'de kayıt değişince Redis kanalı yayın yapar, API kopyaları önbelleği tazeler.
   - Anlamsal önbellek, hız sınırlama (rate limit), kısa ömürlü oturum/onay durumları.
   Neden Faz 3: tek kopyada gerekmez; Kubernetes'te çok kopya, Dapr ve SignalR ölçeği gelince gerekir. Kalıcılık (AOF) açılmazsa veri kaybı kabul edilebilir olmalı.
   Kurulum: önce compose/kind içinde tek pod; Redis'te lisans notu: sürüm ve lisansa (Valkey alternatifi) kurulumdan önce bakılacak.
6. Ölçek ve ürün: SQL vektör indeksi, reranker, anlamsal önbellek, model yönlendirme, OCR, arayüzde çok ajanlı akışın gösterimi.

## Faz 4: durum tabanlı gerçek zamanlı arayüz (kullanıcı önerisi)
1. **SignalR hub'ı**: sunucu, iş/ajan durumunu (kuyrukta, çalışıyor, araç çağırıyor, onay bekliyor, tamamlandı, başarısız, iptal) arayüze iter.
   Arayüz olayları tek tek biriktirmek yerine **durumdan çizilir**: bağlanınca anlık görüntü (snapshot) gelir, sonra değişiklikler.
   Bağlantı kopup dönünce arayüz kaldığı yerden toparlanır (Faz 3'teki dayanıklı işlerle uyumlu).
2. Çift yönlü eylemler: iş iptali, araç/sandbox onayı (insan onayı), alt ajanı durdurma doğrudan arayüzden.
3. Çok ajanlı akışın gösterimi: koordinatör ve alt ajanların durum ağacı, her birinin araç çağrıları ve ilerlemesi.
4. **Ajan seçimi ve yönetimi arayüzü**: kayıt defterinden gelen ajanlar listelenir; kullanıcı hangilerinin çalışacağını seçer,
   her ajanın araç/beceri/limit ayarlarını görür ve (yetkisi varsa) düzenler; çalışan işlerde hangi ajanın ne yaptığı canlı görünür.
5. Ölçek: Kubernetes'te birden çok API kopyası için backplane (Redis, bkz. Faz 3 madde 5) ya da yapışkan oturum; Dapr pub/sub → API → SignalR grupları.
   Sohbet token akışı SSE'de kalabilir (tek yönlü akış için yeterli); SignalR durum/eylem kanalı olur. (varsayım)

## Açık sorular
- Sandbox imajı: `docker pull python:3.12-slim` (yaklaşık 50 MB) yapılabilir mi? Yapılana dek `run_python` dürüst bir hata döner.
- Değerlendirme baseline'ı: ilk koşu gözden geçirilip `--update-baseline` ile referans yapılsın mı?
- Yargıç modeli: ajanla aynı model (`qwen3:8b`) mı kalsın, daha büyük/farklı bir model mi denensin?
- Sandbox'ta riskli işlemler için kullanıcı onayı (insan onayı) ne zaman: faz 3 güvenlik maddesiyle birlikte mi?