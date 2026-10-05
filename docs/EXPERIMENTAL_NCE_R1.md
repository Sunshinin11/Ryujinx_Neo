# Ryujinx Neo Experimental — rodada NCE R1

Base: `d24e22d2d2714fecdae89377f1261ac49d79e3b1`. Identificador: `nce-r1-xzr-branch-20261002`.
Escopo aprovado: primeira rodada pequena, após a auditoria de 02/10/2026. Não reutiliza patches das rodadas descartadas.

## Alterações funcionais

1. `NcePatcher.CreatePatch`: MRS com destino XZR dos registradores já virtualizados TPIDR_EL0, TPIDRRO_EL0 e CNTPCT_EL0 vira NOP. O helper de leitura de contexto usava ADD-immediate com destino 31, que significa SP nessa instrução. Evitar esse helper impede alteração indevida de SP. CTR_EL0 recebe o mesmo tratamento somente no macOS, onde já era virtualizado; no Android continua nativo. MSR TPIDR_EL0 com origem XZR continua escrevendo zero, e leituras para X0–X30 mantêm seus helpers. Leituras desconhecidas permanecem intactas.
2. `NceCpuCodePatch.Write`: antes da primeira escrita de instruções, calcula e valida todos os branches de ida e retorno. Exige alinhamento de quatro bytes e deslocamento no intervalo inclusivo [-134217728, +134217724] bytes. Endereços intermediários usam aritmética verificada. Endereço desalinhado ou alcance inválido provoca exceção; não há fallback novo para JIT nem trampoline longo. A ordem existente de escrita/publicação e a proteção RX permanecem.

Não são correções de FPS comprovadas. Uma ocorrência de `discardedMrs` ou um erro de alcance confirma um caminho relevante, mas não estabelece a causa das regressões históricas.

## Instalação e dados

- Nome: **Ryujinx Neo Experimental**.
- APK desta rodada: variante `MainlineArmv8Release`, pacote `org.kenjinx.android.experimental`.
- Versão: `0.0.2-nce-r1`, código `20109`.
- Flavor optimized, se construído separadamente: `org.kenjinx.android.experimental.optimized`; não usar nesta comparação.
- Namespace Kotlin/Java e símbolos JNI continuam `org.kenjinx.android`, pois são nomes de classes, não a identidade de instalação.
- Authorities dos providers já derivam de `${applicationId}`. A compilação externa deve confirmar esses valores no manifest final.
- Pasta padrão: `/storage/emulated/0/Android/data/org.kenjinx.android.experimental/files/`. O caminho efetivo aparece no log de inicialização.
- Preferências, atalhos, permissões e arquivos de dados pertencem ao novo pacote. A leitura de `neo_flags.txt` agora usa a pasta configurada do próprio aplicativo. Não há migração automática de configurações, saves, firmware, keys ou cache do Current.

O pacote diferente permite coexistência com `org.kenjinx.android`. A coexistência real depende da validação do APK final e ainda não foi testada no aparelho. Pacote diferente também pode receber tratamento de desempenho diferente pelo fabricante; configurações e cache novos são fatores que precisam ser controlados na comparação.

## Telemetria NCE

Ativa por padrão apenas neste código experimental. Para desligar, colocar `nce_telemetry=0` em `neo_flags.txt` na pasta de dados do Experimental e **encerrar completamente/reabrir o aplicativo**. `nce_telemetry=1` ativa; arquivo ausente ativa. A variável de ambiente `NEO_NCE_TELEMETRY`, quando definida, tem precedência. O valor é lido uma vez por processo. Não alterar flags do Current.

Não habilita `DetailedTelemetry` do renderer. Não muda frequência do relógio convidado, flags GPU, sinais, protocolo de encerramento, PROT1, permissões de memória ou filtro de exclusivas.

Logs locais existentes em `files/Logs/` recebem os marcadores abaixo. A identidade Android também é acrescentada a `experimental_session.txt` e ao logcat. Não envia informações pela rede.

| Marcador | Conteúdo e significado |
|---|---|
| `NEO-NCE-R1-BUILD` | Identidade da biblioteca nativa desta rodada; não substitui o hash/proveniência do APK. |
| `NEO-NCE-R1-ANDROID` | Pacote, versão, modelo/Android, pasta e opções globais; overrides por jogo podem diferir. |
| `NEO-NCE-R1-PATCH` | Bytes e quantidade de sites SVC/TLS/CNTPCT, MRS descartados e exclusivas convertidas. São sites estáticos por seção, não execuções. |
| `NEO-NCE-R1-RELOC` | Endereços, tamanho e maior distância dos branches efetivamente aceitos. |
| `NEO-NCE-R1-SESSION` | Backend NCE efetivamente criado, base convidada, frequência virtual, frequência de amostragem do host e estado PROT1. Ausência deste marcador não comprova NCE; verificar também os logs existentes do backend. |
| `NEO-NCE-R1` | Resumo periódico, aproximadamente a cada cinco segundos. `seconds` é o intervalo real; dividir contagens por esse intervalo para obter taxas. |
| `NEO-NCE-R1-SVC` | Histograma de IDs de SVC no intervalo. IDs fora de 0–255 ficam em `other`. |
| `NEO-NCE-R1-THREAD-END` | Totais da thread depois que a execução nativa retornou; saída abrupta pode impedir esse registro. |

Campos do resumo periódico:

- `svc` e `cntpct`: chamadas aos callbacks gerenciados respectivos. TLS permanece sem contadores de execução, para evitar um novo callback em cada acesso.
- `irqCallbacks`: entradas no callback de interrupção, inclusive se ficar bloqueado; `irqStopReturns`: callbacks que retornaram `Running=false`. Não confirma remoção de todas as referências nativas.
- `requestsWithHandle`: solicitações observadas com handle disponível; `signalsAttempted`: estado anterior zero, caminho que tenta enviar sinal; `managedDeferred`: solicitação enquanto o bit gerenciado estava ativo; `nativeCoalesced`: sinal já pendente sem bit gerenciado. Não mede sucesso do envio nem latência até a entrega.
- `trackingReadFaults`/`trackingWriteFaults`: entradas em `MemoryEhMeilleure.VirtualMemoryEvent` na thread NCE registrada. Não conta todo SIGSEGV, falhas em outras threads, nem garante que o evento foi resolvido.
- `map`/`unmap`, `guestMprotect`/`trackingMprotect`: entradas nos métodos de memória após vincular a sessão ao memory manager. São tentativas de operações, inclusive as que falhem, e não quantidade exata de syscalls. Não inclui mapeamentos anteriores à criação da sessão nem mudanças feitas por outros caminhos.
- `svcSamples`, `svcWallMeanUs`: duração de parede média das chamadas amostradas (uma em 256). Inclui espera/bloqueio do serviço e desagendamento; não é tempo puro de CPU nem um percentil.
- `tickSamples`, `tickWallMeanUs`: duração média do getter virtual amostrado (uma em 1024); exclui o trampoline e a transição nativo/gerenciado.
- `active` e `exited`: execuções NCE registradas e finalizadas nesta sessão. Snapshot aproximado; campos não são capturados numa transação conjunta e solicitações concorrentes próximas à saída podem não entrar no total final.

### Custos e limitações

Não há lock/Interlocked adicional por SVC, leitura do contador ou evento de tracking. Há incrementos locais, acesso TLS e verificações de ponteiros; duas leituras extras de Stopwatch por chamada amostrada. Não foi verificado no Android se essas leituras utilizam vDSO ou syscall, nem medido seu custo em nanossegundos.

Cada tentativa de Map/Unmap/Reprotect/TrackingReprotect instrumentada acrescenta um incremento Interlocked. Pode causar tráfego de cache/barreiras e contenção quando muitas threads alteram memória; o código nativo gerado ainda não foi inspecionado. Nenhum mprotect, mmap, flush de cache de instruções ou pausa do convidado foi acrescentado pela instrumentação.

Há aproximadamente 2 KiB de histograma por execução ativa, mais objeto/contadores; o coletor aloca resumos/strings e emite até duas linhas por intervalo. O registro de execuções usa um lock curto na entrada/saída e durante a cópia dos contadores. O timer serializa seus próprios callbacks. Leitura Volatile dos contadores ocorre no coletor. Não faz I/O sob o lock do registro, mas o logger existente pode bloquear com fila cheia, sobretudo nas linhas de entrada/saída. Não promete custo zero nem um teto absoluto de pausa.

O coletor não acessa ponteiros dos contextos nativos. Falha no resumo periódico desativa o timer; ausência continuada de linhas precisa ser investigada. Não cria crash dump confiável para todo encerramento abrupto. Instrumentação de clocks, estados de sinais, publicações de código, NRO e registradores permanece incompleta. Não houve medições em AYN Thor/Adreno 740/Turnip.

## Validação

Nesta preparação: revisão do diff, análise de sintaxe C# sem compilação, parse de XML e conferência estática da identidade. Esses checks não validam binding C#/Kotlin, trimming/AOT, Android manifest final nem comportamento em ARM64.

Preparados testes `NcePatchSafetyTests`: MRS XZR dos registradores tratados, CTR condicional por plataforma, MRS não tratado, manutenção de TLS real e MSR XZR, alinhamento e limites positivos/negativos imm26, endereços altos sem wrap e rejeição de uma relocação posterior/retorno antes de qualquer escrita. Não foram executados aqui.

Antigravity deve executar:

```powershell
dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj -c Release --filter FullyQualifiedName~NcePatchSafetyTests
```

Depois reconstruir a biblioteca nativa com o código desta pasta e montar `assembleMainlineArmv8Release`; verificar hashes, manifest e assinatura. Proibir reutilização de APK ou `libkenjinx.so` das rodadas antigas. Não aceitar APK só porque o label foi atualizado.

No aparelho, em etapa posterior autorizada pelo usuário: confirmar coexistência, abrir/sair/reabrir dez vezes e alternar jogos, retornar do background, observar crashes e integridade dos saves. Comparar BOTW, TOTK, Metroid Prime 4, MK11 e dois jogos previamente estáveis nas mesmas cenas/configurações/versões de jogo e driver. Registrar temperatura inicial/final, perfil de energia, duração, cache frio/quente separado, FPS e distribuição de frame times. Fazer pelo menos três repetições e comparar telemetria ligada/desligada no mesmo APK/pacote, com reinício completo entre as duas condições.

Critérios de interrupção/reversão: qualquer novo crash reproduzível, corrupção visual/saves, falha de inicialização que não ocorria na base, ou regressão repetida fora da variabilidade do cenário. Como referência inicial, investigar perda mediana de FPS >3% ou aumento de p95 de frame time >5%; ajustar ao ruído medido antes de concluir. Validar os contadores com ações concretas (entrada/saída e jogo em NCE) antes de inferir causalidade.

## Independência e reversão

A referência de recuperação `recovery/nce-r1-before-20261002` aponta à base antes das alterações; não contém as alterações desta rodada. O trabalho permanece sem commit. A lista/hash dos arquivos da rodada fica no manifesto entregue pelo Codex. Evitar reset global, que poderia apagar trabalho posterior ou os arquivos pessoais não rastreados.

- Para avaliar custo da telemetria, desligar só `nce_telemetry`; correções e pacote permanecem.
- Para isolar XZR, reverter apenas o tratamento de MRS XZR em `NcePatcher.CreatePatch` (manter contadores, atualizando o marcador de rodada numa nova build).
- Para isolar alcance, reverter a pré-validação em `NceCpuCodePatch` numa rodada própria. Não voltar a publicar um deslocamento inválido: essa build comparativa só é aceitável se os logs comprovarem alcance válido nos módulos do cenário.
- Para remover a rodada completa, restaurar somente os arquivos rastreados listados no manifesto a partir da recuperação, depois de preservar qualquer edição posterior; remover os novos arquivos desta rodada somente após conferir que ainda correspondem aos hashes do manifesto. Recompilar externamente. Não alterar Current nem dados do aparelho.

Sinais/Dispose, lifetime nativo, protocolo de publicação de código, NRO, tracking e substituições de relógio permanecem para rodadas posteriores com evidências e validação próprias.
