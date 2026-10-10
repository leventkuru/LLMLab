# Yol haritası

Durum: **Faz 1 tamam.** Faz 2 planlanıyor. Değişiklikler kullanıcı kararıyla yapılır; varsayımlar "(varsayım)" ile işaretlidir.

Her fazın mimari diyagramı: [DIAGRAMS.md](DIAGRAMS.md).

## Faz 1: temel (tamam)
Katmanlı `agent-api` (Agent Framework, oturumlar), RAG (hibrit arama, SQL Server `VECTOR`), OpenTelemetry + Aspire Dashboard,
eski `LlmApi` kıyas tabanı, Docker/Compose/kind (şimdilik eski API için). Bkz. [ARCHITECTURE.md](ARCHITECTURE.md).

## Faz 2: ajan yetenekleri ve birlikte çalışabilirlik
1. Örnek bir ajana farklı araçlar eklemek.
2. **Kod çalıştırma sandbox'ı**: ajan kod yazar ve izin verilen sınırlar içinde çalıştırır.
   Varsayılan tasarım: tek kullanımlık Docker konteyneri, `--network none`, salt okunur dosya sistemi, bellek/CPU/süre sınırı,
   izin listesindeki dil ve paketler (önce yalnızca Python), riskli işlemlerde kullanıcı onayı, her çalıştırma için telemetri.
3. **MCP**: araçları ve sandbox'ı MCP sunucusu olarak sunmak; ajanın MCP istemcisi olarak tüketmesi.
4. İki ajanın birbiriyle konuşması.
5. **Koordinatör ajan** ve alt ajanlar: alt ajan oluştur, cevabı al, **durdur**.
   Alt ajan görevi bir **port** (başlat / sonucu al / durdur) arkasında olacak; faz 2'de bellekte, faz 3'te Dapr ile uygulanır.
6. **A2A**: ajanları ayrı servislere bölmek (ayrı konteynerler).
7. **Otomatik değerlendirme hattı** (soru seti + LLM-as-judge + regresyon). (varsayım: faz 2'de; koordinatör eklenince elle doğrulama imkânsızlaşır)

8. **Ajan kayıt defteri (registry)** (kullanıcı önerisi): ajanlar koddan çıkıp veri olur. Her ajan bir kayıt: ad, açıklama, talimat (instructions),
   model ve ayarlar (`think`, `num_ctx`, sıcaklık), izinli araçlar (yerel ya da MCP), beceriler (skills: yeniden kullanılabilir talimat + araç paketleri),
   sınırlar (adım, süre, bütçe), etkin/pasif. Doğruluk kaynağı SQL Server (sürümlü kayıtlar, denetlenebilir).
   `AgentFactory` kayıttan `AIAgent` kurar; araç ve beceri adları bir **katalogdan** çözülür (modelin hayal ettiği araç çalışmaz).
   Koordinatör alt ajanları bu kayıttan seçer. Arayüz: ajan listesi, birini seçip konuşma, "şu ajanlar çalışsın" anahtarları (önce basit sürüm).
   Dikkat: talimatı veri yapmak istem enjeksiyonu yüzeyini artırır; düzenleme yetkisi ve sürüm geçmişi gerekir (Faz 3 güvenlik).

Önerilen sıra: araçlar → MCP → sandbox'ı MCP sunucusu olarak yazmak → **ajan kayıt defteri** → iki ajan → koordinatör ve alt ajan yaşam döngüsü → A2A bölme.
Kayıt defteri, iki ajan ve koordinatörden önce gelir: o noktadan sonra ajanları elle kodlamak yerine kayıttan kurmak gerekir.
Değerlendirme hattı ilk çoklu ajan adımından önce başlar.

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
- Sandbox dili: yalnızca Python mu (önerilen), C# da mı?
- Değerlendirme hattı faz 2'de mi (önerilen) faz 3'te mi?
- Faz 2'ye hangi adımla başlanacak (önerilen: araçlar → MCP → sandbox'ı MCP sunucusu olarak yazmak)?
