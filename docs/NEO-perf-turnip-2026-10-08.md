# Otimizações de desempenho no Turnip (Adreno 740) — 08/10/2026

Medido no AYN Odin 2 Pro (Armada OS), TOTK docked, 1º save (Lookout Landing, noite),
sem mudar frequência de CPU/GPU (afinidade 3-7 já aplicada pelo lançador do Armada).
Fases: 60 s parado + 30 s girando a câmera para a esquerda. Log: `RYUJINX_NEO_FPS_LOG=1`.

## Diagnóstico

- GPU a 680 MHz (máximo) 100% do tempo → limite de GPU.
- Quedas ao girar a câmera = streaming de texturas: 100–220 texturas novas/s; o custo estava
  no backend Vulkan: **80% na alocação de memória** e uploads com buffer temporário por textura.

## Mudanças (todas no backend Vulkan, valem para qualquer jogo no Turnip)

1. **Bloco mínimo de 4 MiB no alocador** (`MemoryAllocator.cs`). O Turnip reporta
   `maxMemoryAllocationCount = 0xFFFFFFFF`; a fórmula `16 GiB / count` dava blocos de 4 bytes,
   ou seja, um `vkAllocateMemory`/`vkFreeMemory` (BO novo do kernel) para cada imagem/buffer.
   Agora usa no mínimo o tamanho que NVIDIA/AMD já recebem (4 MiB). `RYUJINX_NEO_MIN_BLOCK=<MiB>`
   altera (0 desliga).
2. **Upload de texturas pelo staging buffer circular** (`TextureView.SetData`), em vez de criar
   um buffer temporário por upload. Custo no backend: 1,8 → 0,36 ms/MB.
   `RYUJINX_NEO_TEX_STAGING=0` desliga.
3. **Views sRGB↔UNORM reais no Turnip** (`TextureStorage` lista de formatos + nova capability
   `SupportsSrgbMismatchingViewFormat` + `Texture.IsViewCompatible`). Mesma classe UBWC, então a
   compressão é mantida; elimina 2 cópias 1920×1080 por frame. `RYUJINX_NEO_SRGB_VIEWS=0` desliga.
4. (Configuração do usuário) Draw Flush = Reduced, Buffer Readback = Relaxed.

Testado e **descartado**: reverter a Etapa 4B (views de formato livre: perde UBWC, −2 fps);
ignorar feedback loop de cor para reativar GMEM (sem ganho).

## Resultados (mesma cena)

| | parado média | parado p5 | giro média | giro p5 | giro mín | pior frame no giro (p95) |
|---|---|---|---|---|---|---|
| Inicial (Draw Flush/Readback Default) | 25,3 | 22,7–23,5 | — | — | — | — |
| + Reduced/Relaxed | 27,9 | 24,1 | ~22,8 | ~11–15 | ~10 | 110–137 ms |
| Final (1+2+3) | 29,5 | 26,8–27,4 | 26,3 | 21,2 | 20,9 | 75–80 ms |

Controle do item 1 (mesma build): sem bloco mínimo, giro p5 13,5 / mín 13,1 / pior 148 ms;
com 4 MiB, 21,2 / 20,9 / 83 ms. Memória disponível não piorou.

## Rodada 2 (GPU) — investigado e descartado

Perfil de GPU com u_trace do Turnip (`MESA_GPU_TRACES=print`): o tempo é dominado pelos passes
do próprio jogo em 1600×900 (G-buffer), que o autotune do Turnip roda em sysmem.

| Teste | Resultado |
|---|---|
| LRZ: shaders que escrevem depth/buffer sem early-Z (13% dos draws, ex. `38c75e8f`) | semântica do jogo; forçar direção de depth seria incorreto |
| Barreira da Etapa 10 ("Non-framebuffer-space barrier") nos passes de G-buffer | passes com feedback loop real já exigem sysmem; sem ela o autotune ainda escolhe sysmem |
| `TU_DEBUG=gmem` (forçar tiles) | pior: 28,6 vs 29,9 fps |
| Manter até 64 MB de blocos de memória vazios | inconclusivo → revertido |

Controle final (build perf-final): parado 29,9 fps (p5 29,05, mín 28,05); giro p5 ~17–21.

## Mesa 26.3 (Turnip 26.3.0-devel, commit e46ee71) × Mesa 26.2.3 do sistema

Mesma build (perf-final), mesma cena, driver trocado via `VK_DRIVER_FILES` (rodada de aquecimento descartada):
26.3 parado 29,74/29,59 (p5 27,45/25,87), giro 26,27/26,50 (p5 21,99/21,18);
26.2.3 parado 29,22/29,54 (p5 24,58/28,21), giro 26,14/25,26 (p5 20,34/21,32).
Diferença dentro do ruído → mantido o 26.2.3. Driver de teste em `~/Downloads/mesa263-turnip/`.
