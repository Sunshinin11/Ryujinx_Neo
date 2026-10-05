# NCE R3 — busca delimitada do ESR

02/10/2026. Experimental 0.0.7-nce-r3, versionCode 20114, org.kenjinx.android.experimental, Ryujinx Neo Experimental, MainlineArmv8Release/arm64-v8a. Base obrigatória: d24e22d2d2714fecdae89377f1261ac49d79e3b1 + NCE R1 + QoL1/2/3 + NCE R2 aprovadas e sem commit. Não restaurar somente HEAD.

## Changelog e histórico verificados antes da edição

Changelog remoto reconferido na revisão 923510a9558e41aee36a06d70782f9febfd14a97, blob b3b4c60d591eac02720aa5747c919063de60696d. Continua descrevendo código até d24e22d2d. Relatório completo também consultado na mesma revisão. As alterações aprovadas locais posteriores fazem parte da base, embora ainda não constem desse changelog remoto.

Não foram alterados filtro de exclusivas/conversão ordenada (b614253ae, 2a1c968f067914223fa7f1378645c735dea12223), serialização RequestInterrupt/Dispose (dc519b29b9c885491198f8983b2ef9ecab8df2c3), altstack/index/contextos e manutenção de cache da fundação (f24d6f706, 5243dc5af, cd86aa8f9), PROT1 opcional, GPU/Turnip/afinidade ou outras funcionalidades do changelog. R1, R2 e todas as QoL permanecem; o marcador R1 continua congelado. Current não recebeu mudanças.

O loop ESR vem de 36d53819a473d195f9efd1253b2421e8670f6c84 (19/01/2023, então ARMeilleure/Signal/NativeSignalHandler.cs). A mudança Android e8e4c5aa6174e2ef8ddef72894eaba24fc20609e apenas reduziu MaxTrackedRanges de 16 para 8 neste arquivo; esse valor foi mantido. Última edição histórica do gerador é c1e24961f94350a9128a6e8f3022c9d1f1a9e04d, anterior às mudanças Neo de setembro. A R3 acrescenta guardas à busca herdada; não remove nem substitui as soluções Neo.

Fontes: [changelog](https://github.com/Sunshinin11/Ryujinx_Neo/blob/923510a9558e41aee36a06d70782f9febfd14a97/CHANGELOG.md), [gerador auditado](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/ARMeilleure/Signal/NativeSignalHandlerGenerator.cs#L114), [histórico do write flag](https://github.com/Sunshinin11/Ryujinx_Neo/commit/36d53819a473d195f9efd1253b2421e8670f6c84), [Linux ABI](https://github.com/torvalds/linux/blob/v6.1/arch/arm64/include/uapi/asm/sigcontext.h#L111), [ESR condicional](https://github.com/torvalds/linux/blob/v6.1/arch/arm64/kernel/signal.c#L725).

## F10 e condição concreta

GenerateUnixWriteFlag ARM64/Linux percorria headers desde uc_mcontext.__reserved (offset 464) até ESR_MAGIC, sem reconhecer magic/size zero ou limite. Se ESR faltasse, o terminador zero/zero mantinha o mesmo ponteiro: loop infinito. O kernel de referência só aloca ESR com fault_code válido ou durante dimensionamento. Não foi inspecionado o kernel do Thor nem provada ocorrência nos jogos.

O caminho é compartilhado por NativeSignalHandler.InitializeSignalHandler e usado pelo wrapper NCE. O NativeAOT Android usa RID linux-bionic-arm64; a plataforma Linux precisa continuar confirmada na build/execução. Foram mantidas as condições de seleção existentes, sem forçar outro caminho por equivalência presumida.

## Alteração pontual

GenerateLinuxArm64WriteFlag emite IR nativo, sem chamar um parser gerenciado durante o sinal. Busca apenas nos 4096 bytes de __reserved; valida ponteiro/alinhamento/overflow inicial, terminador, avanço não zero, alinhamento e tamanho disponível. Cada avanço reduz remaining por um múltiplo positivo de 16; portanto há no máximo 256 registros, sem teto arbitrário para outros campos da frame.

ESR válido exige sizeof(esr_context)=16 e continua retornando exatamente esr & 0x40: 0 leitura, 0x40 escrita. Header e payload são lidos somente dentro da área. O ESR tem caminho direto antes da validação genérica dos demais records.

A ABI exige fpsimd_context/esr_context/extra_context em __reserved, nunca na extensão. Portanto, EXTRA_MAGIC encerra a busca sem carregar datap, mesmo se forjado. Registros futuros comuns bem formados são pulados; ESR na extensão não é aceito. Não há razão para seguir uma extensão potencialmente enorme para achar algo que a ABI proíbe nela. Este comportamento foi derivado do contrato, não de uma constante presumida de tamanho total da frame.

Ausente/malformado retorna -1, distinto de leitura/escrita. Somente no Linux ARM64, GenerateUnixSignalHandler desvia esse estado para a cadeia anterior existente, antes de qualquer chamada de tracking. Não usa si_code para inventar WnR. O corpo do tracking, a normalização do ESR válido e a cadeia antiga foram preservados. A R3 não corrige outras limitações da cadeia anterior (inclusive configuração default/ignore); ausência de ESR ainda pode levar à ação fatal legítima ou a uma limitação do encadeamento existente. Isso precisa de integração Android controlada, não apenas de um teste do parser.

Demais caminhos macOS/Linux x64/Windows, patch do endereço fault, registro Bionic R2 e trampolines/contextos NCE não mudaram. Em plataformas fora Linux ARM64 não é emitido o desvio novo.

## Referências comparativas

Ryubing local 475615f0431b4995b03c930c5089d20f6f3570e0 possui o mesmo loop ESR sem guardas em src/ARMeilleure/Signal/NativeSignalHandlerGenerator.cs:114. Não é evidência de que ele seja correto e não fornece um backend NCE Android equivalente. Eden d16735f5b618942136d6ab53466e3be0a382c30a usa outro protocolo de contexto; GetFloatingPointState em src/core/arm/nce/arm_nce.cpp:36 procura FPSIMD por size, não ESR. Esse loop também não justifica copiar comportamento para o Neo. Nenhum código de Eden/Ryubing foi transplantado.

## Custos e instrumentação

Correção funcional/robustez, não otimização de FPS. Há novas comparações por fault: quatro verificações na entrada, uma por iteração para remaining, cinco verificações adicionais no caminho genérico, validação de tamanho ESR e desvio de unavailable antes do tracking. Também há contabilidade de remaining e operações de alinhamento. A contagem de instruções/ciclos final depende da compilação/regalloc/predição e não foi medida. Pode haver custo perceptível se o título causar muitos faults; não presumir custo zero.

Nenhum lock, atomic, syscall, barreira de memória, log, alocação ou pausa foi acrescentado na execução do handler. EmitterContext/Compiler alocam somente na geração inicial, como antes. Nenhuma instrumentação gerenciada de erro foi colocada dentro do sinal. Usar contadores R1 de tracking/faults e sessões comparáveis; eles não contam os casos sem ESR que agora pulam tracking, logo não comprovam frequência zero desses casos.

## Testes e validação

Arm64SignalEsrTests prepara 32 casos de IR real: leitura/escrita, ordem/records futuros, ESR ausente, sizes inválidos, limite com página protegida, 256 records, EXTRA com datap inválido, ESR proibido na extensão, ponteiros inválidos/overflow e geração ARM64. Em Windows/Linux x64, executa o parser nativo gerado no host sem instalar ou enviar sinais; gera ARM64 sem executá-lo. A página após __reserved é None. Rodar com watchdog do test runner para capturar loops/AVs. Não equivale a integração kernel/NCE.

Codex fez revisão estática, parsing, diffs e hashes; não compilou, executou a fixture/probe, gerou APK ou acessou o aparelho. Antigravity deve conferir a sobreposição de todos os manifestos, executar probe ESR e ABI R2 no NDK efetivo, testar fixtures e publicar biblioteca nova antes do APK. A base para desempenho é a R2 aprovada, com configs/Turnip/cenas equivalentes. Zeldas, Metroid Prime 4 e MK11 são controles obrigatórios. Nova regressão reproduzível requer retirada somente desta rodada.

## Reversão

Snapshot exato da R2 aprovada em work/nce-r3/before, com hashes integrais before-state.json. Restaurar somente NativeSignalHandlerGenerator.cs e build.gradle desse snapshot, e remover somente Arm64SignalEsrTests.cs e este documento após validar hashes R3 atuais. R1/R2/QoL não são revertidas; não fazer reset/checkout só de d24e22. Mudança posterior precisa ser reconciliada. No aparelho, um APK de retorno pode precisar de versionCode superior e assinatura compatível, sem desinstalar nem apagar dados.
