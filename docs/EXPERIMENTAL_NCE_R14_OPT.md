# R14-OPT — armazenamento de intervalos e medição de custo

Versão **0.0.22-nce-r14-opt / 20129**, Ryujinx Neo Experimental, pacote org.kenjinx.android.experimental. Base real: R14-FIX / 20128. Usuário confirmou desaparecimento dos crashes e relatou queda de FPS no Doom 2016; desempenho da frente continua pendente. Changelog remoto relido e arquivado por hash. Current, QoL e as rodadas anteriores são preservados. Sem rollback.

## Evidência e escopo

O Doom solicita até cerca de 28 mil proteções/s e 14 mil eventos rastreados de escrita/s nos intervalos enviados. A R14 mantinha os contratos em uma única List<Range>; Split e Merge usavam Insert/RemoveAt, deslocando os intervalos posteriores sob um monitor. Os logs anteriores não mediam esse custo. Quantidade de syscalls e cenas não podem ser comparadas diretamente com o início da R14 que crashava; falta um baseline equivalente da R13 com FPS/frame time.

A otimização muda o armazenamento dos intervalos e acrescenta amostragem. Preserva o algoritmo de composição, agrupamento/deduplicação de chamadas, publicação de estados após sucesso, buracos, remap, PROT1, contrato inicial do kernel, permissões de execução e o handler do R14-FIX. GPU, TextureGroup, tracking compartilhado, NRO, sinais e patches de instruções não são alterados.

## Armazenamento em blocos

NceProtectionRangeList armazena folhas de até 128 intervalos e um diretório com contagens acumuladas. LowerBound busca primeiro a folha pelos endereços e depois a posição nela; indexadores têm cache da última folha sob o lock já existente do proprietário. As operações que mantêm os contratos continuam com os mesmos índices globais.

Inserção e remoção deslocam dados nas folhas atingidas, em vez de todos os intervalos posteriores. Folha cheia é dividida; pares só são unidos quando a soma cabe em meia folha. Essa histerese evita alocar/dividir/unir repetidamente quando um intervalo quente oscila na fronteira de uma folha cheia. Inserção no final abre nova folha sem copiar a folha anterior.

O diretório ainda pode deslocar referências e recalcular contagens posteriores. Complexidade de mutação: O(128 + número de blocos), além dos intervalos efetivamente atingidos; não é promessa de O(log n) para toda atualização. A busca por endereço é binária nas duas etapas. Folhas parcialmente ocupadas consomem memória adicional; não há pool nem limite artificial de intervalos. Falha extrema de alocação/rollback de estrutura não foi certificada, assim como já não era na lista anterior.

## Telemetria de tempo

Habilitada pelo mecanismo existente NEO_NCE_TELEMETRY/neo_flags; no Experimental permanece ligada por padrão. A inicialização anexa a tabela à sessão. Marcador [NEO-NCE-R14-OPT], e uma linha [NEO-NCE-R14-PROTECT] a cada cinco segundos, pelo timer existente, fora dos callbacks e do lock da tabela.

Mede separadamente query, map, unmap, guest e track. Amostra uma em 256 operações por thread e tipo, usando contadores ThreadStatic sem arrays/allocação por chamada. Operações raras ou threads transitórias podem não produzir amostras. Measured=False/Samples=0 indica ausência de medida, mesmo que os campos numéricos apareçam zero. Amostragem pode sofrer viés de cobertura; médias não são percentis nem medição exata de CPU.

| Campo por prefixo | Significado |
|---|---|
| Ops | Operações que entraram no monitor, incluindo tentativas que lançam exceção. Validações que falham antes dele não entram. |
| Samples / Measured | Quantidade efetivamente cronometrada no intervalo e presença de amostra. |
| WaitMeanUs | Tempo de parede antes de adquirir o monitor, incluindo o pequeno custo de entrada na função. |
| TableMeanUs | Tempo de parede dentro do monitor menos callbacks cronometrados. Inclui trabalho da tabela, overhead da medição e pausas do scheduler/GC. |
| CallbackPerOpMeanUs | Tempo somado dos callbacks por operação amostrada. Para guest/track, callbacks de proteção. Para map/unmap, o corpo completo do mapeamento/desmapeamento. |
| CallbackCalls / CallbackPerCallMeanUs | Tentativas de callbacks nas amostras e duração média por callback; inclui chamadas que falham. Não é contagem total de syscalls. |
| TotalMeanUs | Soma de espera, tabela e callbacks por operação amostrada. |
| EstimatedTableMs | Média amostrada da tabela × operações totais / 1000. Extrapolação aproximada, não tempo de CPU exato nem porcentagem de um núcleo. |
| *MaxUsLifetime | Máximo entre amostras desde a ativação desta tabela, não máximo do intervalo nem percentil. CallbackMax é a soma dos callbacks por operação. |

Para guest/track, callback inclui MemoryManagerNative.ApplyNativeProtection, contador composto, checagens e API de proteção do host; não isola exclusivamente o tempo do kernel em mprotect. composedMprotect antigo continua contando tentativas totais, e guestMprotect/trackingMprotect continuam contando pedidos.

Campos da estrutura: intervals/blocks no fim de operações observadas; peakIntervals/peakBlocks são máximos observados nessas fronteiras, sem contar picos temporários durante um Split/Merge. movedItems conta cópias/deslocamentos de elementos nas folhas; indexSteps conta entradas de prefixo recalculadas; blockSplits/blockMerges são deltas das divisões/uniões de folhas. Não incluem todos os custos de deslocamento do diretório, clears ou bytes exatos. Permitem distinguir crescimento/fragmentação de duração da API.

Snapshots cumulativos usam Volatile.Read sem adquirir o monitor da tabela no timer. São aproximados: campos podem refletir momentos ligeiramente diferentes de uma operação em andamento. A coleta mantém os locks existentes, acrescenta apenas um lock na ativação inicial e nenhum lock extra por falha/atualização. Não acrescenta Interlocked no caminho quente. Incrementos de operações e métricas da estrutura ocorrem sob o monitor existente; o stopwatch é consultado só nas amostras e em seus callbacks. Há overhead de contadores, branches, barreiras da flag e timestamps amostrados; efeito no FPS ainda não medido. Não há log/loop/syscall de instrumentação explícita por falha, nem alocação explícita por chamada de coleta. Divisões da estrutura podem alocar folhas.

O FIX permanece: depois que o tracking trata o acesso, guest válido permite retry mesmo com tracking novamente protegido. IsEffectiveAccessAllowed não volta a rejeitar esse retry. Contadores de trackingProtectedRetries, guestRevokedFaults e demais falhas são preservados.

## Verificações e limites

Preparados 11 casos da coleção em blocos contra List<int>: edições aleatórias, limites, buracos de índices, remoções cruzadas, reuse vazia, histerese, quantidade de itens deslocados e argumentos inválidos. Acrescentados quatro casos à fixture de permissões: dois seeds com 512 páginas, alterações guest/tracking, map/unmap e oracle por página; dois casos da coleta, incluindo callback que falha sem publicar estado fictício. Total: 47 casos preparados, 15 novos. Esses testes não foram compilados nem executados pelo Codex.

Verificação nesta etapa: parsing C#, revisão de tipos/acessibilidade, equivalência do núcleo de permissões após retirar wrappers de instrumentação, hash das fontes anteriores e correspondência do patch. Sem build/APK/commit/push/aparelho. Não há comprovação de ganho de FPS, tempo real de lock, ocupação da tabela, comportamento do kernel ou eliminação de todas as condições concorrentes. A estrutura ainda usa o mesmo monitor global da tabela; esta rodada reduz manutenção de dados, sem relaxar sincronização funcional.

## Validação

Comparar com R14-FIX na mesma cena do Doom 2016, mesmas configurações/driver/limite e temperatura comparável; jogar além do ponto de crash anterior, fechar/reabrir e verificar os demais jogos aprovados. Manter PROT1 por jogo. Guardar logs completos com o marcador novo e FPS observado; priorizar trackTableMeanUs, trackWaitMeanUs, trackCallbackPerCallMeanUs, intervals, movedItems e indexSteps. Verificar Samples/Measured antes de interpretar zeros. Se espera dominar, investigar contenção; se tabela dominar, verificar manutenção/fragmentação; se callback dominar, examinar o volume e custo das proteções sem retirar o contrato guest. Nenhuma dessas hipóteses fica aprovada apenas por existir um contador.

Rejeitar por crash novo, corrupção, permissões incorretas ou regressão consistente nos controles. Backup desta rodada permite restaurar somente seis arquivos existentes e retirar os três novos, conferindo hashes e conservando qualquer mudança posterior. Isso retorna à R14-FIX, com a correção de crashes preservada. Antigravity recebe somente a tarefa de gerar APK, sem testes/probes/clean/rerun-tasks/publicação nativa duplicada.
