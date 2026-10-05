# R14-FIX — retry de tracking concorrente

Versão 0.0.21-nce-r14-fix / 20128, Ryujinx Neo Experimental, pacote org.kenjinx.android.experimental. Base: fontes reais da R14 reprovada, com MemoryProtectionException pública e qualificação externa do catch conservada. R13 e as rodadas anteriores permanecem aprovadas; R14-FIX ainda exige validação Android. Changelog remoto relido, arquivado e identificado por hash. Current não é alterado.

## Problema corrigido

MemoryManagerNative.HandleGuestTrackingFault tratava a escrita por MemoryTracking.VirtualMemoryEvent e depois exigia que a permissão efetiva ainda permitisse o acesso. RegionHandle.Reprotect pode rearmar R/None em outra thread entre essas etapas, mantendo o contrato guest válido. O retorno zero do handler encaminhava uma falha legítima de tracking ao handler anterior. RegionHandle também altera Dirty antes de obter TrackingLock, portanto o rearmamento pode afetar a proteção já durante o tratamento. O protocolo do tracking permite retry; não promete uma permissão efetiva estável após soltar seus locks.

Os logs enviados confirmam R14, NCE, host de 4096 bytes e PROT1 desligado. Os términos não incluem endereço/stack/ESR finais. O defeito concorrente é demonstrável no código; sua participação em cada crash não está comprovada pelos logs. O ramo de retorno zero por permissão efetiva não tinha contador próprio.

## Implementação

O gate inicial continua verificando o contrato guest. Depois do sucesso do tracking, uma consulta única GetAccessState fornece contrato guest e permissão efetiva sob o mesmo monitor. Se o contrato foi revogado ou a faixa desmapeada, retorna zero. Se o contrato continua válido, retorna o endereço para retomar a instrução, mesmo quando R/None foi rearmado pelo tracking. O próximo acesso pode gerar novo evento de tracking, conforme o protocolo existente na R13.

Permissão efetiva passa a ser diagnóstico, não condição de encaminhamento ao handler anterior. GetAccessState também serve aos getters anteriores, conservando sua semântica. Map/unmap, composição de permissões, proteção inicial, PROT1, classificação de sinais, patches e NRO permanecem como estavam. Catch tipado e encaminhamento de erros reais permanecem; não há catch geral novo. A lacuna de restauração em chamadas diretas de UnmapProcessCodeMemory, descrita no diagnóstico, não foi modificada nesta correção específica.

## Telemetria

Marcador [NEO-NCE-R14-FIX]. Novos contadores agregados por thread e por intervalo:

- trackingProtectedRetries: tracking declarou sucesso, guest permite acesso, mas a proteção efetiva ainda o bloqueia. Inclui rearmamento ou proteção remanescente; não demonstra isoladamente qual thread a mudou.
- guestRevokedFaults: contrato guest ausente/revogado na consulta após o tracking, inclusive unmap.
- unhandledTrackingFaults: tracking retornou falso.

rejectedGuestFaults continua contando o gate inicial; failedProtectionFaults continua contando as exceções tipadas. Não há log individual, syscall adicional, loop gerenciado ou alocação de callback por falha. A consulta pós-tracking usa um value tuple de dois bools, não um objeto de resultado. O monitor é adquirido duas vezes por falha tratada, como na R14 original; os dois estados são obtidos na mesma segunda aquisição. Adicionam-se operações de bits/booleanos e incrementos por thread nos novos ramos, sem novo Interlocked/lock no caminho do handler. Custos de contenção e FPS ainda não foram medidos.

## Verificação e validação

Três casos preparados adicionais cobrem o snapshot após rearmamento R de escrita, rearmamento None de leitura e revogação/unmap após o tracking; a fixture totaliza 32 casos. Eles usam o mapa e callback simulado, sem sinal ou mprotect real, e verificam a distinção entre os dois estados. O vínculo desses estados com a decisão do handler foi revisado estaticamente. Esses testes não reproduzem nem certificam a execução nativa/concorrência Android.

Codex realiza somente parsing C#, comparação dos métodos preservados, hashes das fontes e correspondência do patch. Sem compilação, execução de testes, APK ou aparelho nesta etapa. Antigravity recebe somente a tarefa de gerar o APK, sem clean, testes, probes, auditorias, rerun-tasks ou publicação nativa duplicada.

Validar nos mesmos jogos/cenas/configurações da R14 que falhou, mantendo PROT1 por jogo. Priorizar as duas sessões de TOTK e a aplicação 0100416004c00000 representadas nos logs, depois MK11 e os demais jogos relatados. Testar além do ponto de crash anterior e fechar/reabrir. Guardar sessão completa e os novos contadores. Crash persistente exige endereço/stack nativa via logcat/tombstone para distinguir este caminho das outras hipóteses; não tratar o contador isolado como prova. Não prometer que esta correção elimina todos os crashes antes da validação.

São sete arquivos desta correção, sem alteração de GPU, TextureGroup, compactação, QoL ou Current. Backup anterior e patch estão em work/nce-r14-fix e outputs. Nenhum rollback realizado.
