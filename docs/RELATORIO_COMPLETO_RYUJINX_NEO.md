# Ryujinx Neo — Relatório técnico completo do projeto

- **Projeto:** Ryujinx Neo, fork Android derivado do Kenji-NX
- **Repositório:** [Sunshinin11/Ryujinx_Neo](https://github.com/Sunshinin11/Ryujinx_Neo)
- **Branch analisada:** `neo-v2-nce2` no GitHub
- **Último código publicado:** [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db) (05/10/2026)
- **Código das mudanças de 28/09 publicado:** cinco commits integrados ao GitHub em 29/09/2026
- **Mudança de 29/09:** caminho VP9 via FFmpeg e otimizações NEON no NVDEC, publicado no GitHub
- **Mudança de 30/09:** correções VP9, controle do caminho FFmpeg, atalho de saída, áudio, idioma, pilha e teclado, publicada no GitHub
- **Mudanças de 01/10:** retorno à lista de jogos, leitura de NSZ/XCZ, conversão de NSP para NSZ e correções de segurança e cache comprimido de NCZ sólido, publicadas no GitHub
- **Mudanças de 02/10:** suporte NSZ/XCZ e conversão NCZBLOCK no desktop
- **Mudanças de 03/10:** promoção NCE e melhorias Android de mods, idioma e arquivos comprimidos
- **Mudanças de 04/10:** compatibilidade NCE, áudio OpenAL e gerenciamento de memória da GPU publicados na Current
- **Mudanças de 05/10:** opção por jogo “Termos offline” para o applet de navegador offline, com BioShock como caso de compatibilidade
- **Data desta revisão:** 05/10/2026
- **Versão Android no código:** `0.0.24`, `versionCode 20138` ([build.gradle](https://github.com/Sunshinin11/Ryujinx_Neo/blob/neo-v2-nce2/src/KenjinxAndroid/app/build.gradle))
- **Dispositivo histórico de referência:** AYN Thor, Snapdragon 8 Gen 2, Adreno 740
- **Plataforma em foco:** Android arm64-v8a / Linux Bionic, Vulkan em Turnip/Adreno

> As seções 5–10 e as métricas por jogo preservam o histórico até 20/09. As seções 11–17 registram as mudanças do Current publicadas até 05/10, distinguindo o que os commits relatam das medições históricas. Esta revisão descreve somente o código da Current e não repetiu testes de jogos.

---

## 1. Resumo executivo

O Ryujinx Neo é um fork Android do Kenji-NX com trabalho próprio no backend Vulkan, NCE, compatibilidade gráfica e interface. A linha **Neo Current** está em `neo-v2-nce2`; o código Android declara versão `0.0.24` (`versionCode 20138`). O [changelog](../CHANGELOG.md) lista as mudanças por período, incluindo o trabalho funcional de 28/09 publicado na branch.

As frentes implementadas na branch incluem:

1. **GPU/Vulkan:** worker de submissão, ajustes de sincronização, cache de pipelines e texturas, caminho VTG via Compute, offset exato de texture buffer no Turnip, upload 3D assíncrono e chaves de compatibilidade.
2. **CPU/NCE:** arquitetura Neo 2, correções de sinais, contexto e transições nativas, patching de módulos dinâmicos e composição de permissões guest/tracking, além da proteção opcional e conversão seletiva de acessos exclusivos. Em 04/10, o NCE recebeu backing memfd no Android, janelas físicas menores e fallback JIT para títulos de 36 bits.
3. **Android:** identidade Ryujinx Neo, biblioteca e perfis por Title ID, gerenciador de drivers, controles, mods por pasta, idioma por jogo, atalho para voltar à lista, conversão de NSP para NSZ, descompactação NSZ/NCZ e otimização de NSZ sólido.
4. **Diagnóstico:** categorias de log configuráveis, com nove chaves desligadas por padrão na linha Current e controles Guest/Debug expostos no aplicativo.
5. **Vídeo/NVDEC:** decodificação VP9 via FFmpeg com cabeçalhos recompostos, escolha global/por jogo e retorno ao caminho em C# em falhas; correções no decodificador em C# e NEON em etapas do VP9 no ARM64.
6. **HLE/serviços:** prioridade de idiomas suportados, região de pilha ampliada quando há espaço, diálogo de teclado, comandos 12/17 de settings e leitura de conteúdo NSZ/XCZ; o caminho desktop também reconhece e converte NSZ sólido para NCZBLOCK.

Os benchmarks e relatos anteriores a 23/09 continuam úteis como contexto, mas não representam uma medição do snapshot analisado. Em especial, a tela preta de *Marvel Ultimate Alliance 3* no NCE era uma observação anterior às mudanças [NCE-PROT1](https://github.com/Sunshinin11/Ryujinx_Neo/commit/b614253ae) e [TEXBUF-ALIGN1](https://github.com/Sunshinin11/Ryujinx_Neo/commit/f0a4f81be). Os commits relatam correções para esse caminho; esta revisão não executou o jogo para confirmar o resultado final.

Em 28/09, a linha Current recebeu nova página de configurações por jogo, interface responsiva, correções de seleção de DLC/atualizações, modo sem internet por jogo, ajustes NCE/memória e envio seletivo de regiões de texturas 2D grandes. Em 29/09, [`d805ae2bf`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d805ae2bf214f1906183c61861da2f1bbed856f3) acrescentou VP9 via FFmpeg e NEON. Em 30/09, [`6c51417ab`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6c51417ab065d72271b217f3e150c618861c1498) reuniu correções VP9, controles Android e ajustes HLE. Em 01/10, [`4597726eb`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/4597726ebe979df02831dcc6acfc69ee5a3c3ca3) mudou o atalho para voltar à biblioteca e protegeu o fechamento duplicado; [`abd14ca31`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/abd14ca31b225b55930384744efd884f73efe8f0) acrescentou leitura de NSZ/XCZ e conversão de NSP para NSZ; [`d24e22d2d`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d24e22d2d2714fecdae89377f1261ac49d79e3b1) reforçou a conversão e substituiu o cache descomprimido de NCZ sólido por blocos comprimidos. Em 02/10, [`06b96f2a7`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c) ampliou o caminho desktop de NSZ/XCZ e a conversão de NCZ sólido para NCZBLOCK. Em 03/10, [`783e07fd9`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/783e07fd96587d430b53a97c46c60dd4fb885e65) publicou as correções de NCE e as novas ações Android de mods, idioma e arquivos comprimidos. Em 04/10, [`68b46c9a4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/68b46c9a49c5610bed47d9827a462297a37550c8) acrescentou ajustes de memória/NCE, áudio OpenAL e gerenciamento de memória da GPU.

Em 05/10, [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db) acrescentou a opção por jogo **Termos offline**, desligada por padrão, para concluir o applet offline usado na tela de termos de *BioShock*.

---

## 2. Identidade, origem e escopo

### 2.1 Origem

O fork parte da linha Android/Bionic do Kenji-NX, que por sua vez deriva do ecossistema Ryujinx/Ryubing. O trabalho do Ryujinx Neo não substitui os créditos nem as licenças upstream.

| Elemento | Papel no projeto |
|---|---|
| Ryujinx | Base arquitetural original do emulador |
| Ryubing | Continuidade comunitária e fonte de correções estudadas |
| Kenji-NX | Base Android/Bionic do fork atual |
| Gezinjinx/Ryujinx Neo anterior | Fonte de ideias e implementações experimentais de submissão, NCE e GPU |
| Eden/Strato/ChimeraNX | Referências comparativas de comportamento, NCE e interface; não fazem parte do repositório |
| Ryujinx Neo atual | Fork mantido no repositório `Sunshinin11/Ryujinx_Neo` |

### 2.2 Objetivo

Criar uma versão Android do Ryujinx que seja:

- eficiente em SoCs Snapdragon/Adreno;
- estável em sessões reais de jogo;
- configurável por título;
- adequada a dispositivos portáteis como Thor, Odin 2 e Odin 3;
- visualmente própria e simples de usar;
- transparente sobre mudanças experimentais e riscos de regressão.

### 2.3 Fora do escopo atual

- distribuição de jogos, firmware ou chaves;
- inclusão de APKs no repositório;
- mistura de código de outros forks sem seleção e atribuição;
- otimizações universais sem validação mínima;
- uso do JIT como padrão enquanto o custo térmico permanecer alto.

---

## 3. Repositório, branches e pontos de recuperação

### 3.1 Repositório verificado

A branch consultada no GitHub é `neo-v2-nce2`; o último commit de código examinado é `783e07fd9`. Na branch **neo-v2-nce2**, cinco commits de código criados em 29/09 registram os 18 arquivos alterados em 28/09, agora devidamente integrados e publicados no GitHub ([`339470edc`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/339470edc8b891c3994a36931027750380fa835e), [`526f03540`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/526f035405a021819b071fa3be19332a1193d506), [`dc519b29b`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/dc519b29b9c885491198f8983b2ef9ecab8df2c3), [`6283a0ca4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6283a0ca4fa79d390c53eba70488cbff4ca69d9e), [`e39e26833`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/e39e26833225516be048a725d42927269b434d86)).

### 3.2 Linhas e tags históricas

| Referência | Papel registrado |
|---|---|
| `libryujinx_bionic` | Linha Android/Bionic de referência |
| `neo-v2-baseline` | Baseline experimental NEO V2 |
| `neo-v2-fase1` | Etapas históricas de desempenho Vulkan/GPU |
| `neo-v2-nce2` | Linha Current documentada aqui |
| `neo-v2-mua3-stable-pre-nce2-20260919` | Snapshot anterior ao NCE Neo 2 (`f8d0b8e06`) |
| `neo-v2-nce2-r3-pre-ui-20260919` | NCE2 R3 e Loop Safety antes da reforma visual (`7cb21885e`) |

As referências antigas são pontos de comparação do histórico. O estado publicado até 01/10 é detalhado na seção 12.

---

## 4. Arquitetura resumida

```text
Interface Android / Jetpack Compose
        |
        | preferências globais e por Title ID
        v
KenjinxNative.kt / JNI / LibKenjinx
        |
        +--> HLE, memória e serviços do Switch
        +--> CPU: NCE ou JIT
        +--> GPU guest / GAL
                    |
                    v
              Backend Vulkan
                    |
                    +--> shaders SPIR-V
                    +--> pipelines e cache
                    +--> command buffers
                    +--> worker de submissão
                    +--> Turnip / Adreno 740
```

### 4.1 Componentes principais

| Área | Diretórios/arquivos centrais | Responsabilidade |
|---|---|---|
| Aplicativo Android | `src/KenjinxAndroid/app` | Biblioteca, configurações, controles, ciclo do app e JNI |
| Ponte nativa | `src/LibKenjinx` | Criação da sessão e transporte das opções Android para o core |
| CPU | `src/Ryujinx.Cpu` | NCE, JIT, sinais, contexto e relógio |
| GPU guest | `src/Ryujinx.Graphics.Gpu` | Estado da GPU emulada, cache e shaders |
| Tradutor de shader | `src/Ryujinx.Graphics.Shader` | Tradução e geração SPIR-V |
| Backend Vulkan | `src/Ryujinx.Graphics.Vulkan` | Imagens, pipelines, barreiras, render passes e submissão |
| HLE | `src/Ryujinx.HLE` | Kernel e serviços do sistema emulado |
| Código comum | `src/Ryujinx.Common` | Memória, temporização, logging e utilidades |

---

## 5. Método de desenvolvimento e regras operacionais

O projeto adotou um fluxo rígido depois que builds experimentais demais passaram a consumir tempo sem produzir informação suficiente.

### 5.1 Fluxo esperado

1. Analisar código, comparação upstream e logs existentes.
2. Definir uma hipótese que possa ser confirmada ou rejeitada.
3. Fazer uma mudança real acompanhada da telemetria estritamente necessária.
4. Entregar o código para compilação externa.
5. Testar no mesmo aparelho, driver, modo e trecho do jogo.
6. Guardar log e registrar o resultado, inclusive quando negativo.
7. Manter, ajustar ou reverter.

### 5.2 Regras consolidadas

- O Codex **não compila APKs** deste projeto.
- A compilação é feita externamente pelo Antigravity.
- Builds exclusivamente de telemetria são proibidas.
- APKs não devem ser versionados no Git.
- O repositório deve conter apenas o fork trabalhado, sem cópias completas de Ryubing, Gezinjinx, Eden ou outros projetos.
- Mudanças de NCE, Vulkan, shaders e interface devem ser separadas sempre que possível.
- Antes de uma rodada arriscada, criar branch/tag de recuperação.
- Resultado negativo é informação e deve ser documentado.

### 5.3 Variante de build

A variante Android Release no Gradle é `assembleMainlineArmv8Release` (`MainlineArmv8Release`).

A variante `assembleMainlineArmv82Release` fica registrada como histórica. O comando de build não é executado durante alterações feitas pelo Codex; fica sob responsabilidade do compilador externo designado pelo usuário.

### 5.4 Fluxo Experimental → Current

- Toda mudança é implementada e testada no checkout Experimental.
- A promoção ao Current só ocorre após aprovação e ausência de regressões.
- O Current recebe apenas código já validado no Experimental.
- Esta documentação analisa o checkout Current; os registros de teste do Experimental não fazem parte desta revisão.

---

## 6. Fase de desempenho Vulkan — etapas históricas

Esta seção registra as etapas 1 a 23. Ela descreve o histórico da fase, não afirma que cada arquivo permaneceu idêntico depois das limpezas posteriores.

### 6.1 Etapas 1 a 13 — caminho gráfico

| Etapa | Mudança | Resultado histórico | Decisão na época |
|---|---|---|---|
| 1 | `Present` e `BindSparse` pelo worker | Corrigiu a ordenação do caminho de submissão | Mantida |
| 2 | Timeline semaphores | Reduziu dependência do modelo anterior de fences | Mantida |
| 3 | Espera por evento no `ThreadedRenderer` | Removeu `Thread.Sleep(1)` do caminho estudado | Mantida |
| 4B | Listas de formatos de textura | MK11: 239 para 98 ms; TOTK: 410 para 111 ms nos piores intervalos medidos | Mantida |
| 5 | Clears usando `LoadOp.Clear` quando possível | Menos leitura inútil antes de apagar attachment | Mantida |
| 6 | Cache persistente de pipelines | Carregamento observado de 16,9 s para 0,7 s | Mantida |
| 7 | Trabalho Android por quadro reduzido | Menos JNI e menos operações repetidas por apresentação | Mantida |
| 8 | Criação paralela de pipelines | Compilação historicamente cerca de 44% mais rápida | Mantida, mas sempre sob observação |
| 9 | Fila de referências sem trava | Redução esperada de contenção | Mantida |
| 10 | Preservação de barreiras em feedback loop | Corrigiu o bug visual observado no MK11 | Mantida |
| 11 | Staging buffer de 32 para 128 MB | Neutro nos testes; aumenta pressão de memória | Mantida na fase, mas não é vitória comprovada |
| 12 | Espera de VSync sem busy-spin prolongado | FPS neutro; expectativa de menor custo de CPU | Mantida |
| 13 | Listas compatíveis com a classe UBWC da Adreno | Menos stutter e forte melhora do MK11 | Mantida |

### 6.2 Etapas 14 a 19 — submissão e flush

| Etapa | Mudança | Resultado histórico | Decisão na época |
|---|---|---|---|
| 14 | Batching de submissões Vulkan | Melhor resultado isolado do TOTK na fase: 17,4 FPS no cenário medido | Mantida |
| 15 | Auto-flush de 1 para 3 ms | TOTK caiu para 14,8 FPS | Revertida |
| 16 | Origem dos flushes | Permitiu identificar os pontos que forçavam submissão | Mantida como diagnóstico associado ao sistema real |
| 17 | Sync estrito esperando flush natural | Link's Awakening subiu de 40,3 para 43,8 FPS no teste | Mantida |
| 18 | Cache de texturas maior | Eliminou quedas para 11 FPS em carregamentos do TOTK | Mantida |
| 19 | Remoção de flush duplicado após present | Reduziu submissões; FPS neutro | Mantida na época |

### 6.3 Etapas 20 a 23 — CPU, memória e pipelines

| Etapa | Mudança | Resultado histórico | Estado/risco |
|---|---|---|---|
| 20 | Revisão da `CriticalSection` | Nenhuma mudança aplicada | Análise encerrada |
| 21 | `MemoryOwner` usando `ArrayPool<T>.Shared` | Melhor teste do LA: 45,3 FPS | Mantida |
| 22 | Leitura nativa do contador do host | FPS neutro; houve um crash plausivelmente relacionado em um teste | Exige cautela |
| 23 | Mais estados dinâmicos no Turnip | Resultado inicial modesto; risco de erro gráfico silencioso | Requer validação visual ampla |

### 6.4 Leitura conservadora dos ganhos

**Muito provável:**

- redução do tempo de carregamento do cache de pipelines;
- correção visual do MK11;
- redução de leituras/recarregamentos de textura;
- redução de stutter pelas listas de formato e compatibilidade UBWC.

**Provável:**

- melhora forte do MK11;
- melhora de estabilidade e fluidez do Link's Awakening em determinados trechos;
- redução de overhead de submissão com batching.

**Modesto ou não comprovado:**

- ganho final do TOTK;
- vantagem isolada das etapas neutras em FPS;
- ausência de regressões em todos os jogos.

O relatório original usou muitas execuções únicas. Temperatura, clocks, estado de cache e processos em segundo plano podem alterar bastante os números em um portátil Android. Por isso, os resultados acima são evidências de engenharia, não benchmarks científicos definitivos.

---

## 7. Memory Slack e gerenciamento de memória

O Memory Slack amplia o espaço de endereçamento reservado pelo emulador. A interface foi simplificada para dois estados úteis:

| Estado | Valor enviado ao core |
|---|---:|
| Desativado | 0 GiB adicionais |
| Ativado | 12 GiB adicionais |

O valor passa da interface Android por `KenjinxNative.kt`, JNI e `LibKenjinx`, chegando a `HLEConfiguration` e aos limites de memória do processo emulado.

No estado atual:

- existe opção global;
- existe override por jogo;
- preferências antigas são migradas;
- a interface usa switch em vez de quatro opções grandes;
- o tamanho de DRAM, que é uma configuração diferente, usa um seletor compacto.

Memory Slack não cria RAM física e não deve ser descrito como aumento real de memória do dispositivo.

---

## 8. Investigação do Marvel Ultimate Alliance 3

> Esta seção registra a investigação anterior às correções de 23/09. Para o estado da branch Current, consulte as seções 12 e 13. O experimento `[MUA3-SYNC]` e as hipóteses descartadas continuam apenas como histórico.

### 8.1 Sintomas históricos

- corrupção/explosão de vértices em determinadas versões;
- `VK_ERROR_DEVICE_LOST` na cena inicial de combate;
- tela preta no NCE;
- o jogo aparece no JIT, embora com desempenho muito baixo em determinadas builds de proteção;
- comportamento diferente entre JIT e NCE mesmo com o mesmo driver Vulkan.

Title ID usado na investigação: `010060700AC50000`.

### 8.2 Correção V29

O caminho V29 força o geometry shader conhecido em `0x100719F30` para conversão Vertex+Geometry via compute. No código atual, a ativação pode ser reconhecida pelo log:

```text
[NEOV2-V29] Forcing geometry=0x100719F30 to Vertex+Geometry as Compute
```

Esse tratamento é importante para evitar a corrupção de vértices observada. Entretanto, testes posteriores mostraram que simplesmente pular os draws associados a esse geometry shader não eliminava o `DeviceLost`. Portanto:

- V29 tem valor visual/funcional;
- V29 não foi comprovada como causa nem como correção do hang de GPU original;
- o problema de tela preta no NCE é uma linha separada de investigação.

### 8.3 Hipóteses testadas e descartadas para o DeviceLost

| Hipótese | Teste | Resultado |
|---|---|---|
| O geometry shader `0x100719F30` trava a GPU | Mais de 31 mil draws associados foram pulados | `DeviceLost` continuou |
| Vertex buffer resolvido no momento errado | Correção inspirada nos PRs #92/#105 | Não resolveu |
| Criação paralela de pipelines trava o driver | Etapa 8 desativada no teste | Não resolveu |
| Acesso fora dos limites do buffer | `robustBufferAccess` ativado em Qualcomm | Não resolveu |
| Fence específica do Eden | Teste histórico no fork anterior | Não resolveu |
| Barreira global compute→draw | Experimento `[MUA3-SYNC]` | Não resolveu |

As mudanças experimentais MUA3B, MUA3C, MUA3D, telemetria específica e `[MUA3-SYNC]` foram removidas na limpeza que produziu o snapshot estável anterior ao NCE2.

### 8.4 AHB bus error

O erro AHB apareceu continuamente muito antes do timeout de GPU. O teste com outro jogo estável produziu a mesma assinatura com frequência aproximadamente 3,4 vezes maior. Conclusão atual:

- o AHB error não é um bom gatilho para o crash do MUA3;
- ele provavelmente é ruído/uma particularidade de acesso a registradores do driver;
- não deve continuar como linha principal da investigação do `DeviceLost` sem nova evidência.

### 8.5 Hipótese de trabalho que sobrou

O timeout tinha aparência de um trabalho da GPU que não termina, sem page fault, IOMMU fault ou SMMU fault no intervalo analisado. Possibilidades:

- shader com loop/espera que não converge;
- draw ou dispatch indireto com parâmetros ruins;
- diferença de estado produzida pelo NCE antes do trabalho gráfico;
- erro de tradução/ordem de memória que só aparece no NCE.

### 8.6 Relação com Shader Loop Safety

A proteção contra loops infinitos permitiu atravessar o ponto problemático em uma build, mas o resultado ficou perto de 2 FPS e outros jogos, como Mario + Rabbids Kingdom Battle, apresentaram bugs gráficos. Isso mostrou que a proteção:

- pode evitar hangs reais;
- não é segura como regra global obrigatória;
- precisa ser selecionável globalmente e por jogo;
- deve usar cache de shader separado para não misturar binários protegidos e normais.

---

## 9. Shader Loop Safety selecionável

### 9.1 Funcionamento

Quando ativada, a geração SPIR-V adiciona um contador aos loops de shader e interrompe a execução após 8.192 iterações. O estado é propagado por:

```text
Android settings
  -> KenjinxNative/JNI
  -> LibKenjinx.Graphics
  -> GraphicsConfig
  -> GpuAccessor/HostCapabilities
  -> TranslatorContext
  -> SpirvGenerator
```

### 9.2 Estado atual

- desativada por padrão;
- configurável globalmente;
- configurável por jogo;
- loga `[NEOV2-LOOP-GUARD] enabled; limit=8192` quando ativa;
- caches separados:
  - `shader` com proteção;
  - `shader-no-loop-guard` sem proteção;
- versão de codegen atualizada para impedir reutilização indevida de shaders antigos.

### 9.3 Motivo da chave

O limite artificial muda a semântica do shader. Ele pode impedir um loop realmente infinito, mas também pode cortar um loop longo e legítimo, produzindo texturas, iluminação ou geometria incorretas. Por isso, a escolha por jogo é a solução correta no estágio atual.

---

## 10. NCE Neo 2

> As subseções 10.1–10.7 descrevem a implantação inicial e as hipóteses anteriores. A subseção 10.8 registra o estado promovido em 03/10; as comparações antigas continuam históricas.

### 10.1 Motivação

O NCE deveria executar código ARM64 do jogo quase nativamente no ARM64 do Android, reduzindo o custo do JIT. Na prática observada, o NCE original e as primeiras revisões do Neo 2 não entregaram essa vantagem e ainda apresentaram problemas de compatibilidade.

### 10.2 Introdução do NCE Neo 2

Commit principal: `f24d6f706` — `nce: introduce Neo 2 execution and signal architecture`.

Principais áreas alteradas:

- `NceCpuContext.cs`;
- `NceExecutionContext.cs`;
- `NceNativeContext.cs`;
- `NceNativeInterface.cs`;
- `NcePatcher.cs`;
- `NceThreadContextIndex.cs`;
- `NativeSignalHandler.cs`;
- `UnixSignalHandlerRegistration.cs`;
- assembler ARM64.

Objetivos técnicos:

- melhorar preservação e restauração de contexto;
- associar falhas ao contexto correto da thread;
- tornar a manipulação de sinais mais previsível;
- usar pilha alternativa para sinais;
- compactar o snapshot usado no caminho de falha;
- reduzir operações perigosas durante manutenção de cache.

### 10.3 Correções posteriores

| Commit | Correção |
|---|---|
| `5243dc5af` | Removeu definição duplicada de `_alternateStackMemory`, que impedia compilação |
| `cd86aa8f9` | Evitou manutenção de cache NCE antes do mapeamento de código |
| `d29a1ebf5` | Adicionou proteção R3 de flush e integrou Shader Loop Safety selecionável |
| `7cb21885e` | Corrigiu o escopo da capability usada na geração da proteção de loop |

O marcador atual esperado é:

```text
[NEOV2-NCE2-R3] backend active; boundedContextIndex=True; altSignalStack=128KiB; compactFaultSnapshot=True
```

### 10.4 Falhas de compilação já corrigidas

1. Campo `_alternateStackMemory` definido duas vezes em `NceExecutionContext`.
2. Referência a `parameters` fora do escopo correto em `SpirvGenerator`.

Esses erros foram problemas de código/integração, não resultados de runtime.

### 10.5 Observações históricas e mudanças posteriores

Nos testes anteriores a 23/09, *MUA3* mostrava menu e depois tela preta no NCE enquanto o JIT renderizava; *Metroid Prime 4* apresentava stutter no NCE, e *Pokémon Legends: Z-A* avançava melhor no JIT. No mesmo conjunto de testes, *TOTK*, *Link's Awakening* e *MK11* tiveram desempenho parecido nos dois modos. O teste com Turnip T30 não isolou o driver como causa única. Esses resultados pertencem às builds e trechos testados naquela fase.

Depois disso, a branch recebeu [NCE-PROT1](https://github.com/Sunshinin11/Ryujinx_Neo/commit/b614253ae), opção **desligada por padrão** que reaplica rastreamento de memória da GPU após mudanças de permissão em NCE, e [TEXBUF-ALIGN1](https://github.com/Sunshinin11/Ryujinx_Neo/commit/f0a4f81be), que mantém o offset exato da view de texture buffer no Turnip quando o driver anuncia alinhamento de um texel. O primeiro commit relata correção da tela preta de *MUA3*; o segundo relata eliminação de `DeviceLost` na entrada do gameplay. A chave de proteção NCE é seletiva porque pode trazer regressões em outros títulos, incluindo *Metroid Prime 4*.

A conversão `LDXR/STXR` para `LDAXR/STLXR` passou a usar [filtro de instruções](https://github.com/Sunshinin11/Ryujinx_Neo/commit/2a1c968f06); o commit relata correção do boot de *Mario + Rabbids Sparks of Hope*. As mudanças foram validadas no Experimental antes da promoção ao Current; esta revisão não repetiu os testes.

### 10.6 Hipóteses prioritárias para o NCE

1. **Manutenção de cache de instruções/dados:** flush ou invalidação excessiva pode explicar stutter; falta de invalidação correta pode explicar código incorreto.
2. **Proteção de páginas e sinais:** `mprotect`, SIGSEGV e rastreamento de escrita podem provocar pausas muito frequentes.
3. **Estado de contexto:** registradores, FP/SIMD, flags, TLS ou contador podem divergir do JIT.
4. **Ordem de memória:** o NCE pode expor uma corrida que o JIT serializa acidentalmente.
5. **Patcher/trampolines:** blocos específicos podem ser patchados ou retomados de forma incorreta.
6. **Pacing:** parte do stutter pode vir da interação entre threads NCE, GPU e SurfaceFlinger, não do custo bruto da instrução.

### 10.7 Direção recomendada

- usar o JIT como oráculo de comportamento, não como padrão final;
- comparar NCE e JIT no mesmo trecho, cache e driver;
- medir eventos já existentes em uma build que também contenha uma correção real;
- começar pelo custo e frequência de cache maintenance, page protection e signal recovery;
- comparar o NCE atual com implementações conhecidas do Eden/Strato sem copiar mudanças em massa;
- manter cada tentativa reversível e protegida por tag.

---

### 10.8 Correções promovidas em 03/10

O commit [`783e07fd9`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/783e07fd96587d430b53a97c46c60dd4fb885e65) incorpora ao Current as correções funcionais das rodadas Experimental aprovadas. O patcher trata XZR, alcance dos desvios e publicação dos trampolins, inclusive em módulos NRO carregados dinamicamente. O caminho Android de sinais passou por ajustes de ABI, encadeamento com handlers anteriores, associação do contexto à thread, preservação de registradores e transições entre execução guest, sinal e código gerenciado. O encerramento solicitado durante o tratamento de sinal tem caminho de saída nativa.

A proteção de memória NCE separa o contrato de permissões do jogo do rastreamento de escrita/leitura e compõe ambos antes de aplicar a proteção efetiva. Em falhas de tracking, o R14-FIX permite repetir um acesso ainda autorizado pelo jogo quando outra thread rearma a proteção; se o contrato foi revogado ou a região desmapeada, não o aceita. A R14-OPT troca a lista única de intervalos por folhas de até 128 entradas, reduzindo o deslocamento de itens em mudanças frequentes de proteção, sem retirar o monitor que serializa a tabela. O Current mantém essas regras funcionais sem os contadores e timers de investigação do Experimental. [Escopo da promoção](CURRENT_PROMOTION_2026_10_03.md).

---

### 10.9 Memória e compatibilidade NCE publicadas em 04/10

O commit [`68b46c9a4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/68b46c9a49c5610bed47d9827a462297a37550c8) passa a preferir `memfd` como backing de memória compartilhada no Android arm64, com fallback para `ashmem` quando necessário. O mapeamento físico em janelas de 512 KiB procura reduzir a fragmentação de VMAs em títulos que mapeiam memória repetidamente; falhas de mapeamento agora são reportadas. Reservas de páginas são limitadas e reclamáveis. O caminho NCE usa memória espelhável mesmo com o gerenciador seguro; títulos cujo espaço de endereços é de 36 bits usam JIT no lugar do NCE.

No carregamento e na execução, o código acrescenta leitura de NSO comprimido com ZBIC, patching de um segundo bloco em textos grandes e preservação do handle de thread em TLS. Essas mudanças têm como alvo situações de compatibilidade específicas; o commit não estabelece ganho geral de desempenho ou resolução de todos os problemas NCE.

---

## 11. Interface Android e identidade visual

### 11.1 Configurações por jogo

Commit-base da funcionalidade: `762b4af08` — `android: redesign NEO library and add per-game profiles`.

Foi criado `GameSettingsStore.kt`, indexado por Title ID. O sistema oferece:

- perfil global;
- perfil personalizado por jogo;
- fallback seguro para títulos sem perfil;
- proteção para IDs inválidos/homebrew;
- carregamento tanto pela biblioteca quanto por atalho/intenção;
- atualização contextual das configurações durante a sessão.

Opções abrangidas incluem, entre outras:

- NCE/JIT;
- PPTC;
- Memory Slack;
- Shader Loop Safety;
- cache de shaders;
- recompressão de textura;
- Macro HLE;
- resolução e anisotrópico;
- VSync;
- controle virtual, preset e estatísticas;
- Termos offline, opção por jogo desligada por padrão, acrescentada em 05/10 para o fluxo de termos do applet offline.

### 11.2 Persistência durante o jogo

- o controle virtual respeita o estado salvo;
- a sobreposição de estatísticas respeita o estado salvo;
- essas opções não devem reaparecer ou desaparecer a cada abertura sem ação do usuário;
- o menu permanente inferior foi substituído por gesto a partir da borda esquerda;
- a gaveta rápida permite alterar controle, estatísticas, VSync, movimento, Amiibo e abrir as configurações do jogo.

### 11.3 Biblioteca e dois layouts

**Modo NEO:**

- cartuchos inspirados na mídia física do Nintendo Switch;
- sete colunas no alvo paisagem do Thor/Odin;
- capas encaixadas no recorte do cartucho;
- busca em formato pílula;
- sem nome abaixo dos cartuchos;
- efeito de pressão/foco com escala, elevação, sombra e contorno;
- botão para trocar de layout no canto inferior direito.

**Modo Switch:**

- capas horizontais/quadradas em carrossel;
- hora, Wi-Fi e bateria no canto superior direito;
- barra inferior com atalhos circulares coloridos para CPU, gráficos, controles, dados e sistema;
- botão para voltar ao modo NEO no canto inferior direito.

A escolha do layout é persistida em `neoHomeLayout` e a transição usa animação suave.

### 11.4 Configurações redesenhadas

- fundo claro e identidade branca/cinza/grafite;
- menu principal por categorias;
- barra lateral persistente nas páginas internas;
- conteúdo em painel branco;
- apenas seta de voltar, sem botão redundante “Fechar”;
- switches para decisões binárias;
- seletores compactos para listas longas, como tamanho de DRAM, idioma e layout de controle;
- descrições revisadas para explicar o efeito real sem prometer ganhos;
- traduções adicionais para português;
- popup de jogo mais horizontal, em duas colunas;
- seleção indicada por contraste, borda e fundo, não apenas por cor suave.

### 11.5 Identidade do aplicativo

- nome exibido: **Ryujinx Neo**;
- logo escura usada nas superfícies claras;
- ícone de notificação próprio;
- ícone adaptativo com área segura e fundo claro para launcher, abertura e tela de recentes;
- ícone antigo do Kenji-NX/Ryujinx removido das ligações principais do manifesto.

### 11.6 Controles

A opção de inverter A/B e X/Y continua disponível, mas agora:

- vem desativada por padrão;
- possui migração única para corrigir instalações antigas;
- continua configurável quando o usuário realmente quiser o layout alternativo.

### 11.7 Estado da reforma visual

A reforma visual de base está commitada em [`54b072361`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/54b072361) e recebeu [polimento posterior](https://github.com/Sunshinin11/Ryujinx_Neo/commit/df38536b2). Em 28/09, o Current acrescentou página própria de configurações por jogo, grade responsiva e card de ações adaptado a telas estreitas. Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes.

---

## 12. Estado do Current até 05/10/2026

### 12.1 Código publicado na branch GitHub

Na branch GitHub, a última alteração de código publicada nesta revisão é [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db). Estão publicados NCE Neo 2, NCE-PROT1 opcional, Shader Loop Safety selecionável, VTG via Compute, upload 3D assíncrono, compatibilidade gráfica 2D por jogo, perfis por Title ID, dois layouts de biblioteca, gerenciador de drivers, opções de sincronização/GPU, orçamento de memória para buffers e texturas e as mudanças de VP9, áudio, entrada, idioma, pilha, teclado, atalho, NSZ/XCZ, NCE e interface de 03/10, além das mudanças de memória, áudio e GPU de 04/10 e da opção de termos offline de 05/10 descritas no [changelog](../CHANGELOG.md).

### 12.2 Mudanças funcionais de 28/09 publicadas no GitHub

As 18 alterações de código feitas em 28/09 foram organizadas nos cinco commits abaixo, publicados no GitHub na branch `neo-v2-nce2`.

| Área | Implementação publicada no GitHub |
|---|---|
| Configurações por jogo | `GameSettingsDialog.kt` foi reorganizado como `GameSettingsPage`, com seções de sistema/CPU, gráficos, controles/interface, compatibilidade e sincronização. `MainView.kt` registra a rota `gameSettings`; `HomeViews.kt` abre a página pelo menu do título. Voltar sai sem salvar; há ações de salvar e restaurar. |
| Layout responsivo | `HomeViews.kt` usa três ou quatro colunas em retrato conforme a largura e sete em paisagem, além de reduzir margens e cabeçalho em telas estreitas. `GameActionsDialog.kt` usa uma coluna no celular e tenta a capa embutida quando não há imagem salva. |
| DLC e atualizações | `DlcViews.kt` e `TitleUpdateViews.kt` deixam de recarregar o estado salvo em cada recomposição; `DlcViewModel.kt` preserva alterações de ativação pendentes ao reconstruir a lista. |
| Modo sem internet por jogo | `GameSettingsStore.kt` guarda uma opção desligada por padrão. `MainViewModel.kt` a aplica no início do jogo e a limpa no fluxo geral; Kotlin/JNI repassam o valor para `NeoGraphicsSettings`. `IGeneralService` responde que nenhum pedido de internet foi aceito quando ligada. Isso não bloqueia toda a rede do Android. |
| Segurança NCE | `NceExecutionContext.cs` faz `RequestInterrupt` e `Dispose` usarem a mesma serialização para todos os títulos, em vez de condicioná-la à opção de compatibilidade 2D. |
| Memória | `KSystemControl.cs` altera de 3173 para 3285 MiB o retorno para os arranjos 4 GiB, 4 GiB SystemDev e 6 GiB AppletDev. |
| Texturas 2D grandes | `TextureGroup.cs` implementa rastreamento por regiões em texturas block-linear elegíveis a partir de 4 MiB, inclusive camadas/mipmaps. Escritas localizadas podem provocar envio só das regiões alteradas; há fallback para envio integral e verificação rotativa por hash de mudanças não notificadas. |
| Logs | `SettingViews.kt` expõe as chaves Guest e Debug na interface, com descrição do custo de Debug. |

Os commits publicados são [`339470edc`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/339470edc8b891c3994a36931027750380fa835e) (texturas 2D grandes), [`526f03540`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/526f035405a021819b071fa3be19332a1193d506) (limite de memória), [`dc519b29b`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/dc519b29b9c885491198f8983b2ef9ecab8df2c3) (sincronização NCE), [`6283a0ca4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6283a0ca4fa79d390c53eba70488cbff4ca69d9e) (NIFM por jogo) e [`e39e26833`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/e39e26833225516be048a725d42927269b434d86) (interface Android, DLC, atualizações e logs).

### 12.3 Evidência e limites

- O texto da opção “Modo sem internet” cita *DOOM 2016* como motivação. A opção altera apenas a resposta do NIFM e não bloqueia toda a rede. Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes.
- O envio de texturas foi validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. Imagem, memória, tempo de quadro e fallback permanecem itens de monitoramento para futuras promoções.
- Os cinco commits de 28/09 foram publicados no GitHub. Os links da seção 12.2 apontam para a implementação analisada.
- `[MUA3-SYNC]`, `mua3-diag.txt`, robust buffer access forçado e pulo de draws do geometry shader continuam apenas como histórico de experimentos removidos.

---

### 12.4 VP9 no NVDEC em 29/09

O commit [`d805ae2bf`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d805ae2bf214f1906183c61861da2f1bbed856f3) acrescenta um caminho de decodificação VP9 pelo FFmpeg. Como o NVDEC fornece os tiles sem o cabeçalho VP9 completo, o novo `Vp9HeaderComposer` recompõe os cabeçalhos e usa as probabilidades do decodificador em C#. O contexto FFmpeg configura threads por tile e as opções `low_delay` e `fast`. O caminho aguarda quadro-chave para iniciar, mantém uma fila de um quadro e retorna ao decodificador em C# se houver falha; quando o FFmpeg está indisponível, permanece no caminho em C#.

No ARM64, a convolução de 8 taps do decodificador VP9 em C# usa NEON quando as condições do bloco permitem, como largura múltipla de oito e ausência de escala; a escrita de croma usa intercalamento vetorial. Os caminhos existentes continuam disponíveis nos demais casos. A implementação altera 13 arquivos distribuídos entre `Ryujinx.Graphics.Nvdec`, `Ryujinx.Graphics.Nvdec.FFmpeg` e `Ryujinx.Graphics.Nvdec.Vp9`.

O commit relata, no vídeo de abertura de *Zelda: Link's Awakening* no AYN Thor, redução de 15–16 ms para 3–6 ms por quadro após validação no Experimental. Esta revisão não repetiu a medição. A observação não estabelece ganho geral em outros vídeos, aparelhos ou jogos.

---

### 12.5 Correções e controles publicados em 30/09

O commit [`6c51417ab`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6c51417ab065d72271b217f3e150c618861c1498) altera 19 arquivos. No decodificador VP9 em C#, a contagem de coeficientes volta a atualizar as estruturas reais; o mapa de segmentação é preservado entre quadros inter, e quadros intra limpam vetores de movimento antigos. O commit relata que essas correções resolvem o vídeo de abertura de *GRID Autosport*. A revisão do código confirma as alterações, mas não repetiu o jogo.

No Android, a opção global **VP9 pelo FFmpeg** vem ligada por padrão nas preferências. O perfil por jogo pode herdar, ligar ou desligar o caminho; a opção é aplicada ao iniciar o título, e a interface recomenda reiniciá-lo após uma mudança. Com a opção desligada, o VP9 segue pelo decodificador em C#. Esse controle permite comparar os dois caminhos em títulos com vídeo problemático, como o exemplo citado na interface para *GRID Autosport*.

| Área | Alteração implementada |
|---|---|
| Saída por controle em 30/09 | Um botão ou combinação de até quatro botões podia ser gravado em Controles. Nessa versão, o atalho encerrava o aplicativo; em 01/10 passou a voltar à lista de jogos. |
| Áudio | Controle de 100% a 200% na aba Sistema, persistido no Android e aplicado como ganho do ouvinte no OpenAL. |
| Idiomas | Se o idioma desejado não for oferecido pelo jogo, o serviço de aplicação percorre uma ordem de preferência antes do primeiro idioma suportado. Português do Brasil prioriza português. |
| Pilha | Para processos de 39 bits com reserva Android reduzida, a região de pilha pode ser ampliada até 2 GiB quando as demais regiões ainda cabem no espaço de endereços. |
| Teclado | Quando o frontend Android não fornece teclado inline, o applet apresenta um diálogo e devolve ao jogo texto alterado, confirmação ou cancelamento. |

---

### 12.6 Atalho e conteúdo NSZ/XCZ em 01/10

O commit [`4597726eb`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/4597726ebe979df02831dcc6acfc69ee5a3c3ca3) altera o atalho configurável: durante um jogo, ele solicita o fechamento e retorna à lista da biblioteca; fora do jogo, não age. `GameHost`, `MainViewModel`, `LibKenjinx` e o diálogo de saída recebem proteções contra chamadas duplicadas, inclusive para evitar remover a tela inicial da pilha de navegação. A opção da interface passa a se chamar **Atalho para voltar à lista de jogos**.

O commit [`abd14ca31`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/abd14ca31b225b55930384744efd884f73efe8f0) acrescenta leitura de NSZ e XCZ. O HLE envolve contêineres com entradas NCZ em um PFS0 virtual que apresenta os arquivos como NCA. O leitor trata NCZ em blocos com acesso aleatório. O cache de NCZ sólido, introduzido aqui, foi substituído por blocos comprimidos no commit posterior [`d24e22d2d`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d24e22d2d2714fecdae89377f1261ac49d79e3b1). A biblioteca Android reconhece as extensões, e os caminhos de carregamento, atualização e DLC foram adaptados para os formatos comprimidos. `ZstdSharp.Port 0.8.1` é a dependência de compressão adicionada ao projeto HLE.

Na ação **Comprimir** de um jogo NSP, o aplicativo oferece Rápido (nível 8) e Máximo (nível 18), progresso, verificação e cancelamento. O compressor cria um NSZ temporário na mesma pasta, comprime NCA em blocos zstd de 1 MiB e compara SHA-256 dos arquivos ao reler o resultado. Após a verificação, o aplicativo renomeia o arquivo para `.nsz` e tenta excluir o NSP original. Em falha ou cancelamento, remove o temporário e mantém o original; se a exclusão do NSP falhar após a conversão, mantém ambos e informa o usuário.

### 12.7 Segurança da conversão e cache comprimido de NCZ sólido em 01/10

O commit [`d24e22d2d`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d24e22d2d2714fecdae89377f1261ac49d79e3b1) impede que uma falha na validação da descriptografia AES-CTR produza um NSZ inválido. As seções CTR-Ex permanecem cifradas, pois o conversor não aplica contadores por subseção. Quando a conversão não reduz o tamanho, o resultado é tratado como ausência de ganho: o temporário `.part` é removido e o NSP original é preservado. O Android também confere o tamanho do arquivo candidato antes de renomeá-lo.

Para NCZ sólido, o leitor deixa de manter uma cópia integral descomprimida `.bin`. Ele gera por fluxo um cache `.ncb` de blocos comprimidos de 1 MiB. A chave identifica a origem pelo SHA-256 do arquivo comprimido; um checksum lateral `.sha256` e a tabela de blocos permitem rejeitar e reconstruir caches inválidos. A memória mantém até oito blocos residentes. A meta padrão de disco é 4 GiB, com exceção para um único título ativo e sem descartar caches em uso. Arquivos, descompressores e contextos AES são liberados ao encerrar a leitura.

---

### 12.8 Interface e arquivos comprimidos promovidos em 03/10

O commit [`783e07fd9`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/783e07fd96587d430b53a97c46c60dd4fb885e65) acrescenta importação de mods e traduções por pasta selecionada no Android, mantendo a opção ZIP. O conteúdo reconhecido é copiado para o diretório do título com validação de nomes e estrutura; um mod de mesmo nome já instalado não é sobrescrito. O perfil por jogo oferece idioma do sistema emulado próprio ou herança do valor global; a escolha é aplicada quando o jogo inicia. O botão de fechar desaparece após o primeiro toque para evitar pedidos duplicados.

O Android ganha descompactação NSZ→NSP e NCZ avulso→NCA. As conversões usam arquivo temporário, verificam a saída antes de publicá-la e só então tentam excluir a origem; se a exclusão falhar, preservam a saída e informam que ambos permaneceram. O compactador NSP→NSZ produz entradas NCZBLOCK, e a ação **Otimizar NSZ para blocos** converte NCZ sólido em blocos, mantendo entradas já blocadas e demais arquivos. O cache de leitura Android aprovado em 01/10 permanece separado do conversor desktop. [Detalhes das ações Android](QUALITY_OF_LIFE_QOL1.md) · [otimização de blocos](QUALITY_OF_LIFE_QOL4.md).

### 12.9 Caminho desktop de NSZ/XCZ publicado em 02/10

O commit [`06b96f2a7`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c) estende o seletor de arquivos, a biblioteca, o carregamento e a leitura de Build ID para NSZ/XCZ. Antes de iniciar um NSZ com NCZ sólido, a interface desktop chama o conversor para gerar e verificar NCZBLOCK em arquivo temporário e, depois, substituir o arquivo de origem. Um NSZ já em blocos segue sem essa conversão. O código desktop foi preservado na promoção de 03/10 em caminho separado do leitor Android; esta revisão descreve a implementação publicada, sem atribuir a ela validação em todas as plataformas.

---

### 12.10 Áudio, serviços e GPU em 04/10

O commit [`68b46c9a4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/68b46c9a49c5610bed47d9827a462297a37550c8) acrescenta os comandos 12/17 ao serviço de settings. No OpenAL, força saída estéreo, acumula um pequeno volume de amostras antes de iniciar ou retomar após falta de áudio e recupera fontes paradas sem tratar buffers recém-enfileirados como já reproduzidos.

A opção **Memória de GPU estilo Eden** está disponível globalmente e por jogo, ligada por padrão. Buffers e texturas passam a ter orçamento e coleta por uso; dados modificados são sincronizados antes de descartar recursos. Para cópia e limpeza DMA grandes e elegíveis, o código pode atuar na memória guest pela CPU; o caminho GPU permanece quando há sobreposição, recurso residente ou outra condição que impede a via CPU. Os uploads de textura fazem flush a cada 64 MiB. A guarda de alocação e o monitor de RAM solicitam encerramento quando a memória disponível é insuficiente.

Para arrays bindless, há limite de memória, filtro de páginas inválidas ou repetidas e feedback dos índices acessados pelo shader, evitando criar todo o pool de texturas. Descritores são reenviados quando alterados. O fallback de memória global considera bases de descritores usadas por Eden e Ryujinx. A versão de geração do cache de shaders sobe para 7360; com o fallback ligado, o diretório separado `-gmemfb1` evita reutilizar shaders incompatíveis. Os contadores e o feedback que afetam essas decisões permanecem no código, enquanto a telemetria temporária de investigação fica fora da Current.

---

### 12.11 Termos offline por jogo em 05/10

O commit [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db) adiciona **Termos offline** à seção Sistema da página de configurações por jogo. A opção é desligada por padrão, persistida por Title ID e aplicada pelo Android/JNI ao iniciar um título com perfil personalizado ativo. Restaurar o perfil desliga a opção.

Quando ativada para o applet de navegador offline, a implementação consulta o `DocumentPath`, procura o documento no NCA de conteúdo HTML/manual do jogo e extrai URLs `localhost` do HTML e dos scripts próximos. A seleção usa palavras associadas a confirmação e rejeição para escolher uma URL de conclusão, devolvida em `LastUrl`. O carregador registra a partição do jogo para permitir essa leitura também nos caminhos NSP/NSZ e XCI/XCZ.

Para páginas offline com extensão JavaScript e sem URL de conclusão encontrada, o applet retorna `WindowClosed` e `http://localhost/`, atendendo ao fluxo de `window.close()` usado pelos termos de *BioShock*. A opção simula a conclusão dessa tela; não implementa um navegador completo nem bloqueia a rede.

---

## 13. Resultados por jogo e estado da evidência

As métricas abaixo pertencem aos testes históricos da consolidação de 20/09. Os commits posteriores introduzem correções ou opções validadas no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. As observações de VP9 e *GRID Autosport* são atribuídas aos commits de 29 e 30/09.

| Jogo | Observação histórica | Mudança posterior relacionada | Evidência nesta revisão |
|---|---|---|---|
| *Mortal Kombat 11* | Feedback loop corrigido, stutter reduzido e 60 FPS em modo portátil em um teste | Otimizações Vulkan mantidas no Current | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *Tears of the Kingdom* | Melhor observação isolada de 17,4 FPS no trecho pesado; engasgo periódico de ~450 ms | Ajustes de sincronização e GPU | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *Link's Awakening* | Melhor observação histórica de 45,3 FPS no trecho testado; um crash isolado | VP9 via FFmpeg em [`d805ae2bf`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d805ae2bf214f1906183c61861da2f1bbed856f3); controle global/por jogo em [`6c51417ab`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6c51417ab065d72271b217f3e150c618861c1498) | O commit de 29/09 relata 15–16 ms → 3–6 ms por quadro no vídeo de abertura no AYN Thor, após validação no Experimental. Esta revisão não repetiu a medição. |
| *GRID Autosport* | Sem medição histórica registrada aqui | Contadores VP9, mapa de segmentação, vetores de movimento, pilha e teclado em [`6c51417ab`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6c51417ab065d72271b217f3e150c618861c1498) | O commit relata correção da abertura; esta revisão não executou o jogo. |
| *Metroid Prime 4* | Stutter no NCE e problema de textura também visto em JIT | Proteção NCE seletiva e upload 3D assíncrono | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *Marvel Ultimate Alliance 3* | Tela preta no NCE, renderização em JIT; Loop Safety atravessou um ponto com ~2 FPS | NCE-PROT1 e TEXBUF-ALIGN1 relatam correções para caminhos específicos | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *Pokémon Legends: Z-A* | JIT avançou melhor que NCE no teste antigo | Mudanças posteriores de NCE | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *Mario + Rabbids Kingdom Battle* | Loop Safety global causou bugs gráficos; commit posterior relata crashes aleatórios com otimizações de GPU | Loop Safety selecionável e chave “Otimizações de GPU” por jogo | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *Mario + Rabbids Sparks of Hope* | Sem medição anterior registrada aqui | Filtro de acessos exclusivos NCE relatado como correção de boot | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *MARVEL Cosmic Invasion* | Investigação de boot, tela preta e caminhos gráficos 2D | Compatibilidade gráfica 2D em `a24f5d1e7`; serialização NCE ampliada em [`dc519b29b`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/dc519b29b9c885491198f8983b2ef9ecab8df2c3) | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |
| *DOOM 2016* | Espera de rede antes do gameplay motivou a opção | “Modo sem internet” por jogo em [`6283a0ca4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6283a0ca4fa79d390c53eba70488cbff4ca69d9e) | Validado no Experimental antes da promoção ao Current; esta revisão não repetiu os testes. |

O envio seletivo de texturas 2D grandes foi validado no Experimental antes da promoção ao Current; a matriz de regressão para camadas, mipmaps e pequenas regiões deve ser mantida em futuras promoções. Os resultados de desempenho não devem ser generalizados para outros jogos, drivers ou aparelhos.

---

## 14. Telemetria e logs

### 14.1 Estado dos logs

O Current publicado desliga nove chaves de log por padrão e migra preferências antigas ([commit](https://github.com/Sunshinin11/Ryujinx_Neo/commit/bfeab8f9c)). A interface local de 28/09 acrescenta controles visíveis para **Logs do jogo (Guest)** e **Logs de Debug**. Debug avisa sobre custo de diagnóstico. Ativar a chave não garante uma mensagem específica: o caminho precisa executar e a categoria correspondente deve estar habilitada.

Os marcadores `[NEOV2-NCE2-R3]`, `[NEOV2-V29]`, `[NEOV2-LOOP-GUARD]` e `[NEOV2-MEMLIMIT]` aparecem no histórico técnico. Sua ausência em uma sessão só é conclusiva quando o caminho executado e a categoria de log são conhecidos.

A promoção de 03/10 não inclui `NceTelemetry`, timers, sessões e contadores temporários das rodadas Experimental; os logs normais e o contador de FPS continuam disponíveis. [Commit](https://github.com/Sunshinin11/Ryujinx_Neo/commit/783e07fd96587d430b53a97c46c60dd4fb885e65)

### 14.2 Marcadores históricos/removidos

- `[MUA3-SYNC]`;
- `[MUA3-TRACE]`;
- modos de `mua3-diag.txt`;
- telemetria dedicada de command buffers MUA3.

### 14.3 Interpretação responsável

- ausência de mensagem não prova ausência de execução, a menos que o marcador esteja no caminho obrigatório;
- `DeviceLost` é o resultado final, não necessariamente a origem;
- um log sem page fault não elimina erro lógico de shader;
- comparar logs exige mesmo driver, modo CPU, configuração e trecho do jogo;
- linhas suprimidas pelo kernel podem esconder o início de uma sequência de erros.

---

## 15. Metodologia de benchmark recomendada

Para transformar o histórico em evidência de release:

1. Aquecer o aparelho e estabilizar a temperatura.
2. Usar exatamente a mesma rota/save e duração.
3. Rodar pelo menos três vezes cada configuração.
4. Separar cache frio e cache quente.
5. Registrar mediana de FPS, 1% low, frametime P95 e P99.
6. Registrar temperatura e clocks quando possível.
7. Manter driver e modo portátil/dock fixos.
8. Comparar imagens quando houver mudanças em barreiras, pipelines, formatos ou shaders.
9. Testar fechamento e reabertura, não apenas gameplay.
10. Registrar versão do APK/commit junto do log.

### Matriz mínima sugerida

| Jogo | Motivo |
|---|---|
| TOTK | CPU/GPU pesado, flushes e stutter periódico |
| Link's Awakening | Sensível a pacing, estabilidade e tempo de decodificação do vídeo VP9 de abertura |
| GRID Autosport | Abertura em VP9, criação de threads, teclado e escolha do decodificador por jogo |
| MK11 | Feedback loop, UBWC e desempenho gráfico |
| MP4 | Stutter NCE e problema de textura |
| MUA3 | Tela preta NCE, V29 e loops de shader |
| Pokémon Z-A | Compatibilidade NCE |
| Mario + Rabbids KB | Regressão de Loop Safety |

---

## 16. Riscos técnicos conhecidos

| Risco | Consequência | Mitigação atual |
|---|---|---|
| NCE incorreto | Tela preta, stutter ou falha de jogo | Comparação direta com JIT e tags de segurança |
| Loop Safety global | Bugs gráficos | Desativada por padrão e configurável por jogo |
| Dynamic states | Erros discretos de face, profundidade ou stencil | Comparação visual em vários jogos |
| Staging buffer grande | Pressão de memória | Observar dispositivos com menos RAM |
| Cache de textura grande | Maior retenção de memória | Monitorar OOM e troca de jogos |
| Batching agressivo | Dificulta diagnóstico por timeline e pode alterar ordem | Desativar/segmentar em diagnósticos específicos |
| Alterações simultâneas | Impossível atribuir resultado | Uma hipótese principal por rodada |
| Ícone/tema em cache pelo Android | Aparência antiga mesmo após correção | Reinstalação limpa durante validação visual |
| Configuração antiga persistida | Resultado diferente do esperado | Migrações explícitas e log das opções efetivas |
| Envio seletivo de texturas 2D grandes | Imagem incorreta ou custo extra de rastreamento/hash | Comparar imagem, memória, FPS e fallback com envio integral |
| Resposta NIFM por jogo | Jogo reagir de forma inesperada à ausência de internet aceita | Manter a opção desligada por padrão e testar ligada/desligada por título |
| Serialização NCE ampliada | Contenção ou regressão no encerramento/interrupção | Testar sessões longas e abertura/fechamento repetidos |
| Limite de memória 3285 MiB | Menos memória disponível para sistema/applets emulados nos arranjos 4 GiB, 4 GiB SystemDev e 6 GiB AppletDev | Validado no Experimental; monitorar applets e títulos que dependem do pool de sistema |
| VP9 via FFmpeg | Cabeçalho recomposto, atraso de um quadro ou fallback podem afetar vídeos específicos | Comparar vídeo, áudio e tempo de quadro com o decodificador em C#; usar a opção por jogo se houver erro e observar mudanças de resolução |
| Atalho configurável para voltar à biblioteca | Combinação acidental pode fechar o jogo em execução | Escolher combinação deliberada e verificar que fora do jogo o atalho não age |
| Amplificação de áudio até 200% | Distorção em conteúdo com volume alto | Comparar 100% e valores maiores em aparelhos e jogos diferentes |
| Conversão NSP para NSZ | Falha de descriptografia, pouca economia de espaço, cancelamento ou falha de verificação podem impedir a conversão | Validar AES-CTR e SHA-256, comparar tamanhos, descartar `.part` sem ganho e manter o NSP quando a conversão não for concluída |
| Cache de NCZ sólido | Cache corrompido ou pressão de disco pode afetar a leitura | Validar origem e blocos, reconstruir cache inválido e preservar caches de títulos ativos |
| Permissões NCE compostas | Corrida entre tracking e mudança de permissão pode causar falha guest ou acesso indevido | Distinguir contrato guest da proteção efetiva; manter retry apenas para acesso autorizado e testar cenas com muita reproteção |
| NSZ/XCZ em atualizações e DLC | Catalogação ainda pode falhar em alguns arquivos NSZ | Manter a investigação aberta; não declarar suporte a esse fluxo como completamente resolvido |
| Conversões de arquivos | Provedor pode negar rename ou exclusão; operação interrompida pode deixar temporário | Verificar a saída antes de publicá-la e preservar a origem quando a publicação não terminar |
| Memória de GPU estilo Eden | Orçamento ou coleta podem descartar recursos cedo demais; pressão de RAM pode encerrar o jogo | Comparar imagem, consumo e troca de jogos com a opção global/por jogo ligada e desligada; manter sincronização antes do descarte |
| DMA pela CPU e fallback de memória global | Divergência visual ou de leitura em títulos e shaders específicos | Comparar com o caminho GPU e com a opção desligada por título; isolar cache de shaders |
| Áudio OpenAL com preroll | Atraso perceptível ao iniciar ou retomar áudio em alguns títulos | Comparar início, retomada e duração de buffers em sessões longas |

---

## 17. Próximos passos recomendados

### Prioridade 1: acompanhar o código publicado até 05/10

1. Identificar [`68b46c9a4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/68b46c9a49c5610bed47d9827a462297a37550c8) em regressões NCE, áudio e GPU; comparar a opção de memória da GPU ligada/desligada por título, especialmente durante troca de jogos e pressão de RAM. Manter os commits anteriores como referência das mudanças de NSZ/XCZ e interface.
2. Comparar o vídeo de abertura de *GRID Autosport* com VP9 pelo FFmpeg ligado e desligado por jogo; observar imagem, áudio, retorno ao C# e conclusão da abertura. Manter a comparação de *Link's Awakening* com os mesmos parâmetros.
3. Verificar o retorno à biblioteca sem fechamento duplicado; testar conversão NSP↔NSZ, NCZ→NCA e otimização de NSZ sólido com erro, cancelamento e exclusão negada; isolar a falha ainda pendente ao catalogar atualizações/DLCs em NSZ.

4. Acompanhar [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db) no fluxo de termos de *BioShock*: verificar a conclusão com a opção ligada, a persistência por jogo e o comportamento com a opção desligada ou ao trocar de título.

### Prioridades 2 e 3: manutenção da matriz de regressão

Nas futuras promoções, manter os cenários já usados para *MUA3* (NCE-PROT1 e TEXBUF-ALIGN1), *Metroid Prime 4* (NCE/JIT e upload 3D assíncrono), *Mario + Rabbids Kingdom Battle* (otimizações de GPU), *Mario + Rabbids Sparks of Hope* (filtro NCE) e *MARVEL Cosmic Invasion* (compatibilidade gráfica 2D). Registrar também as verificações de interface, DLC/atualizações, resposta NIFM, serialização NCE, texturas 2D grandes, limite de memória, vídeo VP9 no NVDEC, fluxos de controle/teclado e arquivos NSZ/XCZ.

Preservar a metodologia da seção 15: mesmo save e driver, cache frio/quente separado e pelo menos três execuções por cenário. Os resultados históricos continuam limitados aos jogos, aparelhos e drivers em que foram obtidos.

---

## 18. Arquivos-chave para trabalhos futuros

### NCE

- `src/Ryujinx.Cpu/Nce/NceCpuContext.cs`
- `src/Ryujinx.Cpu/Nce/NceExecutionContext.cs`
- `src/Ryujinx.Cpu/Nce/NceNativeContext.cs`
- `src/Ryujinx.Cpu/Nce/NceNativeInterface.cs`
- `src/Ryujinx.Cpu/Nce/NcePatcher.cs`
- `src/Ryujinx.Cpu/Nce/NceThreadContextIndex.cs`
- `src/Ryujinx.Cpu/Signal/NativeSignalHandler.cs`
- `src/Ryujinx.Cpu/Signal/UnixSignalHandlerRegistration.cs`

### Shader Loop Safety e MUA3

- `src/Ryujinx.Graphics.Gpu/GraphicsConfig.cs`
- `src/Ryujinx.Graphics.Gpu/Shader/ShaderCache.cs`
- `src/Ryujinx.Graphics.Gpu/Shader/GpuAccessorBase.cs`
- `src/Ryujinx.Graphics.Gpu/Shader/DiskCache/DiskCacheHostStorage.cs`
- `src/Ryujinx.Graphics.Shader/IGpuAccessor.cs`
- `src/Ryujinx.Graphics.Shader/Translation/HostCapabilities.cs`
- `src/Ryujinx.Graphics.Shader/Translation/TranslatorContext.cs`
- `src/Ryujinx.Graphics.Shader/CodeGen/Spirv/SpirvGenerator.cs`

### Vulkan e performance

- `src/Ryujinx.Graphics.Vulkan/VulkanSubmissionWorker.cs`
- `src/Ryujinx.Graphics.Vulkan/TextureStorage.cs`
- `src/Ryujinx.Graphics.Vulkan/RenderPassHolder.cs`
- `src/Ryujinx.Graphics.Vulkan/BarrierBatch.cs`
- `src/Ryujinx.Graphics.Vulkan/PipelineState.cs`
- `src/Ryujinx.Graphics.Vulkan/PipelineBase.cs`
- `src/Ryujinx.Graphics.Vulkan/AutoFlushCounter.cs`
- `src/Ryujinx.Graphics.Vulkan/Window.cs`
- `src/Ryujinx.Graphics.Gpu/Image/AutoDeleteCache.cs`
- `src/Ryujinx.Graphics.Gpu/Image/TextureGroup.cs` (envio seletivo de texturas 2D)
- `src/Ryujinx.Common/Memory/MemoryOwner.cs`

### Android e configurações

- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/viewmodels/QuickSettings.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/viewmodels/SettingsViewModel.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/viewmodels/GameSettingsStore.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/viewmodels/MainViewModel.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/views/HomeViews.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/views/SettingViews.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/views/GameSettingsDialog.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/views/GameActionsDialog.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/views/GameViews.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/ExitHotkey.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/NszConverter.kt`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/GameHost.kt`
- `src/Ryujinx.Audio.Backends.OpenAL/OpenALHardwareDeviceDriver.cs`
- `src/Ryujinx.HLE/HOS/Applets/SoftwareKeyboard/SoftwareKeyboardApplet.cs`
- `src/Ryujinx.HLE/HOS/Kernel/Memory/KPageTableBase.cs`
- `src/Ryujinx.HLE/HOS/Services/Am/AppletOE/ApplicationProxyService/ApplicationProxy/IApplicationFunctions.cs`
- `src/LibKenjinx/Android/JniExportedMethods.cs` (opção NIFM)
- `src/Ryujinx.HLE/HOS/Services/Nifm/StaticService/IGeneralService.cs` (resposta NIFM)

---

### NVDEC e vídeo VP9

- `src/Ryujinx.Graphics.Nvdec/Vp9FFmpeg/Vp9HeaderComposer.cs`
- `src/Ryujinx.Graphics.Nvdec/Vp9FFmpeg/Vp9FFmpegPath.cs`
- `src/Ryujinx.Graphics.Nvdec/Vp9Decoder.cs`
- `src/Ryujinx.Graphics.Nvdec.Vp9/Decoder.cs`
- `src/Ryujinx.Graphics.Nvdec.Vp9/Detokenize.cs`
- `src/Ryujinx.Graphics.Nvdec.Vp9/Dsp/Convolve.cs`
- `src/Ryujinx.Graphics.Nvdec/Image/SurfaceWriter.cs`
- `src/Ryujinx.Graphics.Nvdec.FFmpeg/FFmpegContext.cs`

### NSZ/XCZ

- `src/Ryujinx.HLE/FileSystem/NczSupport.cs`
- `src/Ryujinx.HLE/FileSystem/NczCompressor.cs`
- `src/Ryujinx.HLE/Loaders/Processes/ProcessLoader.cs`
- `src/Ryujinx.HLE/Utilities/PartitionFileSystemUtils.cs`
- `src/KenjinxAndroid/app/src/main/java/org/kenjinx/android/views/GameActionsDialog.kt`

---

## 19. Histórico condensado de commits do fork

| Commit | Descrição |
|---|---|
| `4c5abb58f` | Port da correção MUA3 V29 geometry-as-compute |
| `e564bb060` | Consolidação inicial das otimizações Vulkan e Memory Slack |
| `f8d0b8e06` | Snapshot do MUA3 anterior ao NCE Neo 2 |
| `f24d6f706` | Arquitetura NCE Neo 2 |
| `d29a1ebf5` / `7cb21885e` | NCE2 R3 e Loop Safety selecionável, depois corrigido |
| `762b4af08` / `54b072361` | Perfis por jogo e reforma visual |
| `df38536b2` / `1d34c02d8` | Identidade Neo e gerenciador de drivers |
| `d517b9276` | Promoção para Neo Current 0.0.1 |
| `b614253ae` / `f0a4f81be` | NCE-PROT1 e offset exato de texture buffer |
| `b9baca871` / `69fd95c78` | Migração do Experimental e upload 3D assíncrono |
| `2a1c968f0` | Filtro de exclusivas NCE e chave de otimizações de GPU |
| `a24f5d1e7` | Compatibilidade gráfica 2D por jogo; último commit de código publicado antes das mudanças de 28/09 |
| [`339470edc`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/339470edc8b891c3994a36931027750380fa835e) | Texturas 2D grandes; publicado na branch `neo-v2-nce2` |
| [`526f03540`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/526f035405a021819b071fa3be19332a1193d506) | Limite de memória 3285 MiB; publicado na branch `neo-v2-nce2` |
| [`dc519b29b`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/dc519b29b9c885491198f8983b2ef9ecab8df2c3) | Serialização NCE; publicado na branch `neo-v2-nce2` |
| [`6283a0ca4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6283a0ca4fa79d390c53eba70488cbff4ca69d9e) | Modo sem internet por jogo; publicado na branch `neo-v2-nce2` |
| [`e39e26833`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/e39e26833225516be048a725d42927269b434d86) | Interface Android, DLC, atualizações e logs; publicado na branch `neo-v2-nce2` |
| [`d805ae2bf`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d805ae2bf214f1906183c61861da2f1bbed856f3) | VP9 via FFmpeg no NVDEC e NEON no decodificador em C#; publicado em 29/09 |
| [`6c51417ab`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6c51417ab065d72271b217f3e150c618861c1498) | Correções VP9, opção FFmpeg por jogo, atalho de saída, áudio, idioma, pilha e teclado; publicado em 30/09 |
| [`4597726eb`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/4597726ebe979df02831dcc6acfc69ee5a3c3ca3) | Atalho retorna à biblioteca e fechamento duplicado protegido; publicado em 01/10 |
| [`abd14ca31`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/abd14ca31b225b55930384744efd884f73efe8f0) | Leitura de NSZ/XCZ e conversão NSP para NSZ; publicado em 01/10 |
| [`d24e22d2d`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d24e22d2d2714fecdae89377f1261ac49d79e3b1) | Validação da conversão NSZ, preservação do NSP sem ganho e cache comprimido de NCZ sólido; publicado em 01/10 |
| [`06b96f2a7`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c) | Biblioteca desktop NSZ/XCZ e conversão de NCZ sólido para NCZBLOCK; publicado em 02/10 |
| [`783e07fd9`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/783e07fd96587d430b53a97c46c60dd4fb885e65) | NCE promovido e melhorias Android de mods, idioma e arquivos comprimidos; publicado em 03/10 |
| [`68b46c9a4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/68b46c9a49c5610bed47d9827a462297a37550c8) | Compatibilidade NCE, áudio OpenAL, memória da GPU, arrays bindless e fallback de shader; publicado em 04/10 |
| [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db) | Termos offline por jogo, desligado por padrão, e conclusão do applet de termos do BioShock; publicado em 05/10 |

---

## 20. Checklist antes de qualquer nova build de desempenho

- [ ] Árvore visual congelada em commit/tag.
- [ ] Commit exato anotado.
- [ ] Nenhuma telemetria sem hipótese e sem mudança real.
- [ ] Driver, jogo, save, modo e configuração definidos.
- [ ] NCE/JIT claramente indicado no log.
- [ ] Loop Safety claramente indicado no log.
- [ ] Memory Slack e DRAM efetivos conhecidos.
- [ ] Cache frio/quente identificado.
- [ ] Resultado esperado definido antes do teste.
- [ ] Critério de manter/reverter definido.
- [ ] APK e artefatos fora do Git.

---

## 21. Glossário

| Termo | Significado neste projeto |
|---|---|
| NCE | Execução nativa de código ARM64 guest no host ARM64 |
| JIT | Tradução dinâmica de código durante a execução |
| PPTC | Cache persistente de código traduzido |
| UBWC | Compressão de largura de banda usada por GPUs Adreno |
| TBDR | Renderização em blocos típica de GPUs móveis |
| Timeline semaphore | Primitiva Vulkan que representa progresso crescente da GPU |
| Batching | Agrupamento de vários envios em uma chamada ao driver |
| Flush | Forçar envio/sincronização do trabalho pendente |
| DeviceLost | Estado Vulkan em que o dispositivo/driver deixa de aceitar o trabalho atual |
| V29 | Tratamento específico do geometry shader conhecido do MUA3 |
| Loop Safety | Limite artificial de iterações em loops de shader |
| Memory Slack | Folga adicional no espaço de endereçamento emulado |
| Title ID | Identificador único do título Switch usado nos perfis por jogo |

---

## 22. Conclusão

O Ryujinx Neo Current não parou nas alterações de 27/09. Em 28/09, o checkout Current recebeu mudanças funcionais em interface Android, DLC/atualizações, NIFM por jogo, NCE, limite de memória, logs e envio seletivo de texturas 2D grandes. O código foi organizado e publicado em cinco commits na branch GitHub em 29/09 (`339470edc`, `526f03540`, `dc519b29b`, `6283a0ca4` e `e39e26833`).

Em 29/09, o commit [`d805ae2bf`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d805ae2bf214f1906183c61861da2f1bbed856f3) acrescentou VP9 via FFmpeg e otimizações NEON no ARM64. Em 30/09, [`6c51417ab`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/6c51417ab065d72271b217f3e150c618861c1498) corrigiu o decodificador VP9 em C#, expôs a escolha global/por jogo e acrescentou atalho de saída, volume, prioridade de idioma, ajuste de pilha e fallback de teclado. Em 01/10, [`4597726eb`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/4597726ebe979df02831dcc6acfc69ee5a3c3ca3) passou o atalho para retorno à biblioteca e protegeu o fechamento duplicado, enquanto [`abd14ca31`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/abd14ca31b225b55930384744efd884f73efe8f0) adicionou NSZ/XCZ e a conversão NSP para NSZ. Ainda em 01/10, [`d24e22d2d`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/d24e22d2d2714fecdae89377f1261ac49d79e3b1) reforçou a validação da conversão, preservou o NSP quando não há economia e introduziu cache comprimido em blocos para NCZ sólido. Em 02/10, [`06b96f2a7`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c) acrescentou o caminho desktop de NSZ/XCZ e NCZBLOCK. Em 03/10, [`783e07fd9`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/783e07fd96587d430b53a97c46c60dd4fb885e65) publicou a promoção das correções de NCE e das novas ações Android de mods, idioma e arquivos comprimidos. Em 04/10, [`68b46c9a4`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/68b46c9a49c5610bed47d9827a462297a37550c8) acrescentou backing memfd e ajustes de mapeamento NCE, serviços e áudio, controle da memória GPU, arrays bindless e fallback de memória global em shaders. Em 05/10, [`1178c711e`](https://github.com/Sunshinin11/Ryujinx_Neo/commit/1178c711e9f07a9b9dde12b90ee42f3ee5eb68db) acrescentou a opção por jogo **Termos offline**, desligada por padrão, para concluir o applet offline usado na tela de termos de *BioShock*. A falha de catalogação de atualizações/DLCs em NSZ permanece pendente. A matriz de regressão permanece como rotina das futuras promoções.
