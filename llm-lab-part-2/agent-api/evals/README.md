# Değerlendirme hattı (evals)

Ajanın davranışını **elle bakmadan** ölçer. Üç katman:

1. **Soru seti** (`cases.json`): her vaka bir soru, hangi ajanla ve "Belgeler" açık mı olduğu, beklentiler.
2. **Deterministik kontroller** (ucuz, tekrarlanabilir): "şu araç çağrılmalı / çağrılmamalı", "cevap şunu içermeli",
   "kaynak olmalı", süre sınırı. Çoğu hata (araç atlandı, uydurma atıf) burada yakalanır.
3. **LLM-as-judge** (isteğe bağlı): bir model cevabı `rubric`/`reference`'a göre 1-5 puanlar (doğruluk, dayanak,
   bütünlük). Bir vaka geçer: tüm kontroller geçti **ve** yargıç doğruluk ≥ 4 ile dayanak ≥ 4 verdi.

Sonuçlar `results/report-<zaman>.json` dosyasına yazılır. `baseline.json` ile karşılaştırılır: **önceden geçen bir vaka
şimdi kalıyorsa ya da geçme oranı baseline'ın altına düşüyorsa çıkış kodu 1** (CI'da build'i kırar).

## Çalıştırma

Önkoşul: servisler ayakta (`agent-api/scripts/dev.ps1`).

```powershell
cd C:\dev\llm-lab\llm-lab-part-2\agent-api
dotnet run --project evals/LlmLab.Evals                              # hepsi, yargıçla
dotnet run --project evals/LlmLab.Evals -- --no-judge                # yalnızca kural kontrolleri (hızlı)
dotnet run --project evals/LlmLab.Evals -- --only smoke              # kimlik ya da etiketle seç
dotnet run --project evals/LlmLab.Evals -- --skip-tags sandbox,docs  # imaj/belge yoksa atla
dotnet run --project evals/LlmLab.Evals -- --update-baseline         # sonucu yeni referans yap
```

## Dikkat edilecekler (dürüst sınırlar)

- **Yargıç ile ajan aynı model** (varsayılan `qwen3:8b`): kendi cevabını kayırabilir; küçük model puanlamada tutarsızdır.
  Bu yüzden deterministik kontroller asıl güvencedir. Mümkünse `--judge-model` ile farklı/büyük bir model kullanın.
- **Sonuçlar deterministik değildir:** aynı vaka bazen geçer bazen kalır. Tek bir kırmızı vakayı hemen "regresyon"
  saymayın; yeniden çalıştırın. Gürültüyü azaltmak için vaka sayısını artırmak, tekrar çalıştırmak (henüz yok) gerekir.
- **Vakalar ortama bağlıdır:** `docs`/`rag` etiketli vakalar yüklü belgeye (`foveated_vonenet_tez_taslagi.pdf`) bağlıdır;
  `sandbox` etiketli vaka Docker imajı ister. Kendi belgelerinize göre uyarlayın.
- İlk koşuda baseline yoktur; bir koşuyu gözden geçirip `--update-baseline` ile referans yapın.
