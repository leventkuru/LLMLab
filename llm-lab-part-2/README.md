# llm-lab

> Mimarinin (telemetri, Agent Framework, Ollama, SQL, Docker, Kubernetes) bütünsel anlatımı: [ARCHITECTURE.md](ARCHITECTURE.md)

Yerel bir LLM (Ollama) ile konuşan, sohbet geçmişini SQL Server'da tutan küçük bir uygulama.

```
React (web) ──► .NET API (LlmApi) ──┬──► Ollama (Windows ana makinesi, GPU)
                                    └──► SQL Server (sohbet geçmişi)
```

## Üç çalıştırma biçimi

| Biçim | Veritabanı | Adres | Başlat |
|---|---|---|---|
| **Geliştirme** | Ana makinedeki SQL Server (`DevDb`, Windows kimlik doğrulaması) | http://localhost:5173 | `dotnet run --urls http://localhost:5080` (LlmApi) ve `npm.cmd run dev` (web) |
| **Docker Compose** | Konteynerde SQL Server (`LlmLab`, `sa`) | http://localhost:8080 | `docker compose up -d --build` |
| **Kubernetes (kind)** | Pod'da SQL Server + kalıcı disk | http://localhost:8081 | `.\k8s\deploy.ps1`, sonra `kubectl --context kind-llm-lab -n llm-lab port-forward svc/web 8081:80` |

Compose ve Kubernetes'i **aynı anda çalıştırmayın**: iki SQL Server, WSL'e ayrılan belleğe (6 GB) sığmaz.

## Önkoşullar

- Ollama çalışıyor ve modeller inmiş: `ollama pull qwen3:8b` ve `ollama pull qwen3-embedding:0.6b`
- Embedding'in CPU varyantı (aşağıdaki "GPU belleği" notuna bakın): `ollama create qwen3-embed-cpu -f ollama/Modelfile.qwen3-embed-cpu`
- `.env` dosyası (git'e girmez): `MSSQL_SA_PASSWORD` ve SQL Server lisansını kabul ettiyseniz `ACCEPT_EULA=Y`
- Kubernetes için küme: `kind create cluster --name llm-lab`

## RAG: belgeler ve vektör arama

Arayüzde **Belgeler** sekmesi: `.txt`, `.md`, `.pdf` yükleyin, belgeler parçalanıp gömülür ve SQL Server'ın `vector(1024)` kolonunda saklanır. Embedding modeli `qwen3-embedding:0.6b` (`ollama pull qwen3-embedding:0.6b`).

Arama kalitesini ölçmek için (parça boyutu, ön ek ya da model değişince çalıştırın). Ölçüm betikleri bu stack'in
konusu dışında kaldığı için `.temp/` altındadır (git'e girmez):

```powershell
.\.temp\rag-eval\eval-rag.ps1                                  # geliştirme API'si
.\.temp\rag-eval\eval-rag.ps1 -Base http://localhost:8081/api  # Kubernetes
.\.temp\rag-eval\ask.ps1 "soru" -UseDocuments -Base http://localhost:5090   # tek soru, olayları özetler
```

`.temp/py-chat/` ilk aşamadaki Python terminal sohbetidir (`.venv` taşındığı için gerekirse yeniden oluşturun:
`py -m venv .venv`).

Embedding modelini değiştirirseniz `ChatDb.EmbeddingDimensions`, `Rag:*` ön ekleri ve bir migration gerekir; farklı modellerin vektörleri karşılaştırılamaz, belgeler yeniden yüklenmelidir.

## GPU belleği: embedding modeli neden CPU'da

8 GB'lık kartta sohbet modeli (`qwen3:8b`, 5,8 GB) ile embedding modeli yan yana GPU'ya yüklenince kart dolar ve
sohbet hızı **42 → 6-9 token/sn**'ye düşer; üstelik model yeniden yüklenmedikçe yavaş kalır. Bu yüzden API,
embedding için `qwen3-embed-cpu` kullanır (aynı ağırlıklar, `num_gpu 0`): sohbet hızı 38-43 token/sn'de kalır,
embedding sorgu başına ~0,2 sn sürer. Daha büyük bir kartınız varsa `Ollama__EmbeddingModel=qwen3-embedding:0.6b` verin.

Ollama sürecinin durumunu görmek için: `ollama ps` (VRAM payı) ve `%LOCALAPPDATA%\Ollama\server.log` (`tg =` satırları token/sn'dir).

## Temizlik

```powershell
docker compose down            # konteynerleri sil, veri diski (mssql-data) kalır
docker compose down -v         # veri diskini de sil
.\k8s\deploy.ps1 -Down         # Kubernetes namespace'ini (ve diskini) sil
kind delete cluster --name llm-lab
```

## Notlar

- Konteynerde Windows kimlik doğrulaması çalışmaz; SQL kimlik doğrulaması (`sa`) kullanılır. Parola yalnızca `.env`'de ve Kubernetes Secret'ında durur.
- Konteynerler ve pod'lar Ollama'ya `host.docker.internal:11434` ile ulaşır.
- API, `MigrateOnStartup=true` ile açılışta tabloları kendisi oluşturur. SQL Server'ın ilk açılışı yavaşsa API bir kez çökebilir; Docker/Kubernetes yeniden başlatır.
