# R14 — F08: permissões guest e tracking no Android NCE

Base real: R13 aprovada pelo usuário, 0.0.19-nce-r13 / 20126. Entrega: 0.0.20-nce-r14 / 20127, somente Experimental. HEAD local d24e22d2d2714fecdae89377f1261ac49d79e3b1, branch neo-v2-nce2, com as alterações locais das rodadas aprovadas. O HEAD sozinho não representa essas rodadas. Changelog remoto relido e arquivado com hash no manifesto. Current, QoL, GPU, texturas, sinais e patches de instruções preservados. A implementação desta frente fica concentrada nesta rodada; aprovação Android continua pendente.

## Evidência e caminho utilizado

No Android ARM64, ArmProcessContextFactory seleciona NceEngine e MemoryManagerNative quando as condições existentes de NCE são atendidas. MapPages em KPageTable cria os aliases físicos; MemoryManagerNative.Reprotect muda a permissão guest; TrackingReprotect(guest: true) protege os mesmos aliases. MemoryEhMeilleure recebe as falhas guest autorizadas pelo dispatcher nativo aprovado na R10FIX e chama MemoryTracking.VirtualMemoryEvent. A R14 substitui somente esse callback no Native Bionic. Não modifica o callback compartilhado dos outros gerenciadores.

No código anterior, TrackingReprotect aplica None/R/RW diretamente. Assim, um pedido de tracking RW pode tornar gravável uma página guest R/None, e um pedido de tracking R pode retirar X de RX. O fallback de VirtualMemoryEvent sem handle também pede RW. PROT1 restaura proteções de tracking depois de Reprotect, mas não compõe o contrato guest e pode reproteger regiões maiores que a mudança guest. MapPages recebia a permissão inicial, mas o Native MapView começava RW sem registrar esse contrato: corrigir apenas Reprotect deixaria essa lacuna.

Referências verificadas no estudo e confrontadas com o estado local aprovado:

- [Neo MemoryManagerNative, d24e22d](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/MemoryManagerNative.cs): Reprotect e TrackingReprotect.
- [Neo MemoryTracking, d24e22d](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Memory/Tracking/MemoryTracking.cs#L112): VirtualMemoryEvent e restauração.
- [Neo VirtualRegion, d24e22d](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Memory/Tracking/VirtualRegion.cs#L86): solicitação de permissões pelo tracking.
- [Changelog Neo](https://github.com/Sunshinin11/Ryujinx_Neo/blob/neo-v2-nce2/CHANGELOG.md): compatibilidade PROT1 e mudanças preservadas.
- [Eden d16735f, ProtectRegion](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/core/memory.cpp#L94): proteção de fastmem e páginas RasterizerCachedMemory; modelo distinto, sem transplante de comportamento.
- [Ryubing 475615f, MemoryManagerHostMapped](https://git.ryujinx.app/projects/Ryubing/src/commit/475615f0431b4995b03c930c5089d20f6f3570e0/src/Ryujinx.Cpu/Jit/MemoryManagerHostMapped.cs#L410): Reprotect e TrackingReprotect usam modelo JIT/mirrors diferente; não demonstra que o mesmo comportamento seja seguro nos aliases executáveis Bionic.

## Correção funcional

NceMemoryProtectionMap mantém intervalos mapeados com contrato guest, último pedido do tracking e indicação de pedido ativo. Permissão efetiva: dados = guest & tracking; execução = guest.X. Tracking só retira acesso a dados, sem conceder permissões guest ausentes e sem retirar X. Atualizações parciais dividem intervalos; buracos não são protegidos pelo tracking. Reprotect exige toda a faixa mapeada.

Map/unmap sincronizam o cadastro com os aliases; remap começa com RW real do novo MapView. KPageTable.MapPages registra imediatamente a permissão inicial por SetMappedPermission, conservando pedidos de tracking já feitos no novo alias. Escritas do loader continuam pela memória física/backing, como antes. Unmap remove o contrato. A proteção do host é aplicada antes de publicar o novo estado do respectivo grupo; falha do callback não publica permissão fictícia.

PROT1 continua desligado por padrão e usa a configuração existente. Com PROT1 desligado, uma mudança guest continua substituindo a proteção de tracking apenas na faixa alterada. O pedido anterior fica registrado; o próximo pedido do tracking volta a compor. Com PROT1 ligado, Reprotect compõe imediatamente com o pedido registrado. A R14 não transforma PROT1 em opção sempre ativa.

O callback verifica endereço e tamanho reais antes de chamar o tracking. Falha de acesso proibido pelo guest retorna zero e segue a cadeia de falhas existente, sem converter o evento em escrita rastreada. Depois do tracking, só retorna sucesso se a permissão efetiva permitir o acesso. Falhas de proteção, região ou argumento retornam zero, evitando repetição falsamente tratada. Classificação ESR, falhas de execução/prefetch, falhas do host e saída de sinais continuam sob a R10FIX/R11 existentes.

Host Bionic com páginas maiores que 4 KiB não pode impor contratos guest distintos de 4 KiB por mprotect. Tanto a decisão de gerar patches NCE quanto a seleção do engine exigem páginas host de 4 KiB; demais tamanhos usam JIT e registram o motivo. O construtor do mapa também rejeita tamanhos diferentes. Essa restrição só afeta Bionic. O tamanho real do Thor não foi consultado no aparelho nesta rodada.

## Desempenho e instrumentação

Deduplicação evita mprotect quando a permissão efetiva não mudou. Intervalos contíguos com mesma nova permissão compartilham uma chamada. Não há promessa de ganho de FPS: a frente é funcional e exige comparação com R13.

Custos adicionais: um monitor por map/unmap ou alteração de proteção; duas consultas sob monitor por falha tratada, uma quando rejeitada. Busca inicial binária, caminhada proporcional às regiões afetadas; inserção/remoção na lista pode deslocar O(n) intervalos. Divisões alocam intervalos; callbacks map/unmap podem alocar closures. Não há nova syscall em SVC/CNTPCT comuns ou consulta de registradores. Um mprotect adicional pode ser necessário ao impor uma permissão inicial diferente de RW. Locks implicam barreiras e podem causar contenção/pausa; custo em microssegundos e efeito térmico não foram medidos. O mapa nunca mantém seu monitor ao entrar em callbacks de tracking/GPU: tracking pode adquirir o monitor de proteção, sem caminho inverso introduzido.

Telemetria adicional: marcador de início [NEO-NCE-R14] com tamanho host e PROT1; composedMprotect conta tentativas reais de proteção composta, incluindo chamadas que falham; rejectedGuestFaults e failedProtectionFaults contam eventos por thread. guestMprotect/trackingMprotect antigos continuam contando pedidos. O contador composto usa Interlocked por chamada; contadores de falhas são incrementos por thread, agregados pelos mecanismos existentes. Sem log individual por falha; intervalo e amostragem anteriores preservados. Contadores não medem duração/contensão do monitor nem provam ausência de falhas.

## Verificação e limites

29 casos preparados com callback simulado: matriz 6 permissões guest × 3 pedidos de tracking; alterações parciais, PROT1 desligado, buracos, remap, falha de map/proteção, deduplicação, gate de endereço, limites e política de páginas host. São testes do contrato em memória, sem sinal, mprotect ou execução nativa. Não foram compilados nem executados nesta etapa. Verificação estática usa apenas parsing C#, revisão de fluxo, hashes e correspondência do patch.

Não verificado: comportamento de sinais/mprotect no kernel Android, execução exclusiva X e eventuais permissões implícitas de leitura da plataforma, contenção/fragmentação em jogos, operações concorrentes com teardown, falha parcial de mprotect em múltiplas VMAs e situações de falta de memória. O estado por grupo é publicado depois do sucesso, mas uma syscall que modifica parcialmente o host e retorna erro não tem rollback comprovado. Mudança guest concorrente entre gate inicial e callback pode produzir notificação de tracking sem escrita efetiva; o gate final impede declarar sucesso com acesso ainda proibido. Não há nova garantia de atomicidade de execução entre unmap/map/reprotect nem leases para threads guest em execução.

## Validação e reversão

Comparar com R13 nas mesmas configurações e cenas: abertura, 10–15 minutos por jogo, FPS/oscilações/travadas, visual, fechar/reabrir e uso de módulos dinâmicos. Priorizar Doom 2016, MK11, os dois Zeldas e Metroid Prime 4; manter Marvel Ultimate Alliance 3 como controle de PROT1. Não habilitar PROT1 globalmente durante a comparação. Registrar versão, tamanho host, opção por jogo e logs da sessão.

Rejeitar se houver crash novo, loop de falhas, perda de execução, regressão consistente de FPS ou corrupção visual nova. Texturas intermitentes do Doom já observadas não são diagnóstico comprovado desta frente; comparar sessões equivalentes. Em host maior que 4 KiB, verificar mensagem de fallback e abertura via JIT, sem patches NCE aplicados.

Reversão independente: restaurar somente os nove arquivos existentes do backup R14 e remover os três novos, verificando hashes atuais antes de qualquer reversão. Isso volta exatamente à R13 aprovada, conservando QoL e as demais rodadas. Não reverter pastas inteiras ou aplicar patches descartados. R14 fecha a implementação F08; aprovação desta build e validação final permanecem pendentes.

## Correção de compilação CS0122

A geração externa do APK falhou porque MemoryProtectionException era interna ao projeto Ryujinx.Memory. A classe passa a ser pública para permitir o catch tipado no projeto Ryujinx.Cpu. Construtores, mensagens, throwOnFail e lógica de proteção permanecem idênticos; não foi ampliado o catch para exceções genéricas. Versão mantida em 0.0.20-nce-r14 / 20127, pois a primeira geração não produziu APK. A checagem anterior foi somente sintática e não verificou acessibilidade entre projetos. Correção revisada estaticamente; compilação externa e validação Android continuam pendentes. A reversão completa desta rodada passa a incluir nove arquivos existentes e três novos.
