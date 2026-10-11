# TOTK: quadrados escuros de 64 px no Turnip (Adreno) — correção via gather bias

## Resumo

Em *The Legend of Zelda: Tears of the Kingdom*, rodando no Linux ARM64 com Turnip
(Mesa, Adreno 740 / Snapdragon 8 Gen 2, ex.: AYN Odin 2 Pro com Armada OS), apareciam
**quadrados escuros de exatamente 64×64 px** (grade de tiles da tela) sobre superfícies
de pedra/terreno iluminadas pelo sol ou pela lua. Eles surgem logo após carregar o
save, numa faixa diagonal do terreno, e somem quando o sol/lua se move.

O mesmo save **não** mostra o problema numa GPU desktop. A causa é uma diferença de
hardware na escolha do bloco 2×2 de texels do `textureGather`, e o Ryujinx já tinha o
mecanismo de compensação (`GatherBiasPrecision`), só que ele não estava ativo para o
Turnip.

**Correção:** ativar `gatherBiasPrecision` também quando o driver é Turnip
(`Ryujinx.Graphics.Vulkan/VulkanRenderer.cs`) e incrementar `CodeGenVersion`
(`Ryujinx.Graphics.Gpu/Shader/DiskCache/DiskCacheHostStorage.cs`) para que o cache de
shaders do host seja retraduzido.

## Causa raiz

Cadeia de renderização do TOTK envolvida (hashes dos shaders guest):

1. `e751605e211e1fce` — sombra do sol em **1/4 de resolução** (400×225 docked). Reconstrói a
   posição do pixel a partir de uma profundidade linear de 1/4 de resolução usando
   vários `textureGather` (TLD4/TLD4S) e compara com o shadow map em cascata
   (D16 `Texture2DArray` 800×800×4, `sampler2DArrayShadow`).
2. `ca301f59…` — blur dessa máscara.
3. `28ba97ce7890f289` — classificador por tile de 64 px: penumbra / totalmente
   iluminado / **totalmente na sombra**.
4. `6a7d805adb0cfaae` (+ variantes) — sombra em 1/2 resolução, executada só nos tiles
   que não estão "totalmente na sombra"; ela marca a flag que habilita a luz do sol na
   iluminação em resolução cheia.

Tiles classificados como "totalmente na sombra" pulam a luz do sol, e o tile inteiro
fica escuro: são os quadrados.

O erro está no passo 1. O shader faz os `textureGather` em coordenadas que caem
praticamente **em cima do centro de um texel**, isto é, na fronteira em que o hardware
decide qual bloco 2×2 retornar. Em GPUs NVIDIA (o Switch), a coordenada é arredondada
para o 1/256 de texel mais próximo antes dessa decisão. No Adreno, um erro mínimo de
ponto flutuante abaixo da fronteira seleciona o bloco vizinho (um texel deslocado). Em
superfícies inclinadas, a posição reconstruída fica deslocada e o `dRef` da comparação
com o shadow map sobe ~100–150 passos de D16 (plano por faceta do terreno). Com isso,
uma faixa inteira do terreno se auto-sombreia na máscara de 1/4 de resolução, o
classificador marca esses tiles como sombra total e eles aparecem como quadrados
escuros.

O comportamento na fronteira é *implementation-defined* no Vulkan
(`subTexelPrecisionBits`), então isso não é um bug do Turnip: o emulador precisa
reproduzir a semântica da NVIDIA. É exatamente o que o `GatherBiasPrecision` já faz
para AMD/Intel (`TexturePass.cs`: soma meio subtexel, `1 / (size * 2^(bits+1))`, às
coordenadas normalizadas de todo gather).

## Evidências (resumo dos experimentos)

Medições feitas no mesmo frame, com dumps do Ryujinx e modos de depuração que gravam
operandos exatos (codificados em bytes RGBA8):

| Teste | Resultado |
|---|---|
| Forçar o resultado dos taps de sombra de `e751605e` = 1 | quadrados somem (mas as sombras reais quebram) → o erro está nesse passe |
| `TU_DEBUG=sysmem,nolrz,noubwc,flushall,syncdraw`, `IR3_SHADER_DEBUG` (opt/sync off) | sem efeito |
| LOD, comparação manual (`texelFetch`), camada forçada, troca camada/LOD | sem efeito |
| Montagem do array de cascatas (cópia GPU das camadas), objeto amostrado | correto |
| Depth bias de hardware dos casters | 0/0/0 (o jogo usa bias no shader) |
| Constant buffers guest × host | idênticos |
| Profundidade de 1/4 = amostra pontual exata da de 1/2 | correto |
| Camada de cascata escolhida por `e751605e` × `6a7d805a` | igual em 98% dos pixels |
| `dRef` de `e751605e` × `6a7d805a` | +100–150 ULP de D16, plano por faceta |
| Margem `dRef − profundidade armazenada` | `6a7d805a` ≈ −200 ULP (iluminado); `e751605e` ≈ −90 com faixa > 0 |
| Mesmo save em GPU desktop (Windows) | **sem quadrados** |
| Arredondar as coordenadas dos gathers de `e751605e` para 1/256 texel (diagnóstico) | **sem quadrados** (2/2 execuções); controle com a mesma build sem o arredondamento: quadrados presentes |
| `GatherBiasPrecision` nativo ativado no Turnip (cache isolado) | **sem quadrados** (2/2) |
| Build final (este patch), cache normal, sem variáveis de diagnóstico | **sem quadrados**; sombras do Link e do terreno normais |

Observação: já existia no fork local um teste opcional
(`RYUJINX_TOTK_TURNIP_GATHER_BIAS=1`). Ele parecia não funcionar porque o bias muda a
**tradução** dos shaders e o Ryujinx carrega o cache de shaders do host sem
retraduzir quando `CodeGenVersion` não muda. Por isso esta correção também incrementa
`CodeGenVersion`.

## Mudanças

- `src/Ryujinx.Graphics.Vulkan/VulkanRenderer.cs`: `gatherBiasPrecision` passa a valer
  `SubTexelPrecisionBits` também com Turnip (`IsTurnip`). A variável de ambiente
  `RYUJINX_TOTK_TURNIP_GATHER_BIAS=0` desliga o bias (para teste A/B).
- `src/Ryujinx.Graphics.Gpu/Shader/DiskCache/DiskCacheHostStorage.cs`:
  `CodeGenVersion` 7357 → 7358, para invalidar o cache de shaders do host.

## Impacto

- **Primeira execução** após atualizar: o cache de shaders do host é reconstruído a
  partir do cache guest (tela de carregamento mais longa uma única vez).
- **Custo em runtime:** cada `textureGather` ganha uma consulta de tamanho de textura e
  uma soma por coordenada, igual ao que já acontece em AMD/Intel.
- **Escopo:** apenas o driver Turnip. O driver proprietário da Qualcomm não é alterado.

## Como validar

1. Carregar um save à noite/ao amanhecer numa encosta de pedra (ex.: Hyrule Ridge) e
   girar a câmera ~45°: antes da correção aparecem tiles escuros de 64 px em faixa
   diagonal; depois, não.
2. Teste A/B: `RYUJINX_TOTK_TURNIP_GATHER_BIAS=0` + apagar/isolar o cache de shaders do
   host faz os quadrados voltarem.
