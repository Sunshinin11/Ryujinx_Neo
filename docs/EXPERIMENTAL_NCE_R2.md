# NCE R2 — contrato sigaction Bionic ARM64

Data: 02/10/2026. Somente Experimental. Versão 0.0.6-nce-r2, versionCode 20113, pacote org.kenjinx.android.experimental, nome Ryujinx Neo Experimental. Variante MainlineArmv8Release. Base de fontes: HEAD d24e22d2d2714fecdae89377f1261ac49d79e3b1 **mais todas as alterações locais aprovadas NCE R1 + QoL1 + QoL2 + QoL3**. Restaurar somente HEAD perderia essas alterações.

## Evidência e escopo

O achado F09 da auditoria identifica SigActionBionic usando SigSet com 128 bytes. No contrato público Bionic LP64, sigset_t tem 8 bytes, flags/handler/mask/restorer ficam em 0/8/16/24 e sigaction tem 32 bytes. Antes da R2, o tipo C# tinha restorer em 144 e tamanho 152. O bloco maior não causava, por esse motivo isolado, overflow: o restorer real podia ser preservado incidentalmente dentro da máscara grande. Não foi demonstrado que essa divergência causasse as regressões dos experimentos descartados.

Referência oficial: https://github.com/aosp-mirror/platform_bionic/blob/731631f300090436d7f5df80d50b6275c8c60a93/libc/include/bits/signal_types.h#L101

Na preparação da R2 foram lidos os cabeçalhos instalados em C:\Android\Sdk\ndk\28.2.13676358 (r28c). O CMakeCache existente de app/.cxx/Release/6x423p6v/arm64-v8a declara esse NDK, API 29 e arm64-v8a. Cache histórico não comprova o NDK da próxima build; Antigravity deve reconfirmá-lo e executar o probe entregue fora do repositório contra o sysroot efetivo.

## Alterações

- SigSetBionic contém uma máscara de 64 bits e é usado exclusivamente pelo caminho Bionic. SigSet/SigAction comuns mantêm seus campos e layout existentes.
- Guarda de tamanho/offsets LP64 antes da primeira resolução de sigaction. Android LP32 não é suportado por esta representação; o APK permanece arm64-v8a.
- Conversões explícitas preservam handler, todos os 64 bits da máscara, flags e restorer. A parte não utilizada da máscara comum fica zerada.
- A importação de sigemptyset para Bionic recebe o tipo correto. Quantidade de chamadas preservada.
- Consulta/instalação recusam resultado nativo inválido antes de usar a ação anterior. A instalação SIGSEGV Android antes não verificava o retorno. A restauração continua retornando bool.
- O erro nativo de uma chamada direta é lido imediatamente via __errno de libc.so somente na falha e publicado em Marshal.SetLastPInvokeError depois da telemetria. Não se confia em um erro P/Invoke antigo.
- Exportações sigaction/__errno são resolvidas uma vez com a trava existente e publicadas juntas com Volatile. A resolução direta em libc.so, evitando libsigchain, foi preservada.
- Telemetria [NCE_SIGNAL_R2], round=nce-r2-bionic-abi-20261002, na consulta/instalação/restauração. Registra layout, operação, sinal, resultado/errno, flags/máscaras e classificação do handler anterior e presença de restorer; não imprime endereços. Usa o controle nce_telemetry/NEO_NCE_TELEMETRY da R1.

## Preservação e custos

Não há alteração no handler de fault, em NceExecutionContext/RequestInterrupt/Dispose, altstack, índice de contextos, TLS, exclusivas, branches, PROT1, NRO, clocks, GPU/Turnip, mods, conversões, idioma por jogo ou UI. O marcador NCE_BUILD/NEO_EXPERIMENT_ID da R1 permanece congelado; versão do APK e marcador específico de sinal distinguem R2.

A R2 não acrescenta syscalls de consulta nem de instalação/restauração: mantém uma sigaction por operação e sigemptyset onde já existia. __errno é uma consulta TLS em libc, não syscall, executada somente na falha. Nenhuma trava adicional; a trava existente fica restrita à primeira resolução. Há uma leitura acquire de ponteiro por operação de registro e uma publicação release inicial. Verificação de layout, uma resolução adicional de símbolo e um log de layout ocorrem uma vez por processo; conversão e pequenos logs ocorrem por operação. O logger existente pode alocar/enfileirar/escrever conforme a configuração, portanto não se presume I/O gratuito. Não existem alterações por SVC, tick, instrução guest ou fault, nem pausas guest adicionais. Não há estimativa numérica de FPS ou latência sem medição.

## Validação pendente

BionicSignalAbiTests contém seis casos: layout contra offsets esperados do NDK, preservação dos layouts comuns e quatro round trips de default/ignore/custom, flags e máscaras altas, restorer e cauda zerada. São testes de layout/conversão; não instalam sinais e não comprovam integração Android. Codex fez análise estática, sem compilar nem executar NUnit/APK.

Antigravity deve executar probe de ABI, testes C# da R1/R2/QoL e testes Kotlin QoL; compilar com publish novo linux-bionic-arm64 e inspecionar biblioteca/APK, pacote, versão, ABI e assinatura. Não reutilizar libkenjinx antiga. Não executar no aparelho nesta etapa.

No Thor, o usuário valida: boots e encerramentos repetidos; fundo/retorno; dois Zeldas, Metroid Prime 4, MK11 e controles anteriormente estáveis; tempos de sessão/configurações/Turnip equivalentes. Ausência de FPS/crash em uma sessão não comprova a restauração da cadeia. GetSegfaultExceptionHandler e RegisterExceptionHandler são usados na inicialização; RestoreExceptionHandler não tem chamador na árvore auditada. Portanto não haverá log de restore no uso normal atual, e isso não deve ser apresentado como teste de restauração. Um teste controlado posterior precisa exercitar instalação/consulta/restauração reais no Android, em processo isolado sem guest, preservando a ação original e incluindo SA_SIGINFO, máscara alta e restorer; não alterar o ciclo de vida normal só para produzir telemetria.

## Reversão limitada

O snapshot exato aprovado está em work/nce-r2/before, registrado no manifesto entregue. Para retirar R2 das fontes, restaurar apenas UnixSignalHandlerRegistration.cs e app/build.gradle do snapshot e remover somente os dois arquivos novos R2, após conferir os hashes atuais. As 35 fontes aprovadas e o Current devem continuar iguais aos hashes anteriores. O patch entregue contém somente R2; não aplicar/reverter patches R1/QoL nem fazer reset/checkout de HEAD. Qualquer alteração posterior nesses quatro arquivos exige reconciliar o diff antes de revertê-los.

No aparelho, voltar para um APK com versionCode menor pode ser bloqueado. Não desinstalar nem apagar dados para contornar isso; uma build de retorno deve usar a funcionalidade QoL3 aprovada com versionCode superior e assinatura compatível, em rodada identificada separadamente. A retirada de R2 das fontes não realiza downgrade no dispositivo.
