# Promoção Current — 03/10/2026

Android **0.0.23 / 20130**, nome **Ryujinx Neo**, mainline **org.kenjinx.android**. Mantém o pacote/assinatura existentes do Current e a separação do Experimental. Base de publicação: neo-v2-nce2, commit 06b96f2a7103bffe44a63bea01e0a774e8ce2d7c. A promoção integra todas as mudanças funcionais aprovadas nesta conversa, incluindo QoL e R14-FIX/R14-OPT; não reaplica versões descartadas.

## Conteúdo

NCE: XZR/branches, ABI Android e sinais, indexação/lifetime, exclusivas, transições/registradores, falhas guest versus host, saída nativa por sinal, snapshots de threads pausadas, módulos NRO dinâmicos e composição de permissões guest/tracking. R14-FIX mantém o retry guest válido mesmo com tracking rearmado; R14-OPT usa a coleção em blocos. GPU, TextureGroup, filas, compactação de textura e demais otimizações publicadas permanecem intactos.

Android: instalação de mods/traduções por pasta e ZIP; idioma por jogo; botão de fechar desaparece após o primeiro clique. Conversão NSZ→NSP e NCZ→NCA; compressão Rápido/Máximo; ação recomendada para otimizar NSZ sólido para blocos; cards com a identidade visual aprovada; publicação de saída verificada antes da exclusão da origem. Se o provedor negar exclusão, a saída permanece e o fluxo informa que restaram ambos os arquivos. Erro/cancelamento anterior à publicação preserva a origem.

## Sem telemetria de investigação no Current

O arquivo NceTelemetry não é promovido. Removidos hooks e observadores, contadores/timers/sessões, medição de SVC/ticks/IRQ/proteções, contadores de cópias/prefixos e banners específicos das rodadas. A tabela preserva o monitor e as regras funcionais, mas não cronometra operações nem consulta flags de telemetria. Testes exclusivos da instrumentação não são promovidos; os oráculos funcionais são mantidos. Não se remove o contador de FPS nem os logs normais já existentes. A telemetria gráfica preexistente continua desligada pelo padrão anterior do Current; seu código não foi reescrito. Experimental permanece byte a byte igual à build aprovada e conserva telemetria.

## Integração da publicação desktop

Os commits remotos de documentação e NSZ do Hugare são integrados por fast-forward antes da promoção. Quatro arquivos de interface desktop permanecem exatamente como publicados, assim como o relatório técnico atualizado. O conversor NszBlockConverter é preservado, compilado apenas fora de ANDROID. O leitor remoto foi movido para DesktopNczSupport.cs, também fora de ANDROID, com renomeação dos quatro tipos para evitar conflitos. NczSupport.TryWrap encaminha ao leitor remoto fora de ANDROID; em ANDROID o corpo aprovado permanece idêntico. NormalizeExtension/HasNczEntries/IsXciExtension/WrapIfCompressed mantêm a interface compartilhada. NativeZstd não entra na compilação Android; a decompression e o otimizador Android continuam usando os seus backends já validados. Não se certificou desktop/Linux ou o empacotamento nativo desktop nesta promoção.

## Evidência e limites

O usuário relatou que a R14-OPT ficou ótima e autorizou levar todo o conjunto ao Current sem telemetria. Aprovação Experimental não substitui compilação do pacote Current, nem certifica todos os jogos e provedores. Verificações locais: hashes do conjunto aprovado, preservação do Experimental e arquivos fora do escopo, regras de proteção e ramos ANDROID, parsing C# e recursos XML. Sem compilação, execução de testes, APK, commit/push ou acesso ao aparelho por Codex nesta promoção. O prompt final delega apenas sincronização final do remoto, geração de um APK, commit dos arquivos previstos e push normal. Sem force-push, testes/probes/clean ou publicação nativa duplicada.

R14/R10 rejeitadas são documentação histórica: apenas as versões corrigidas entram como implementação. Os documentos EXPERIMENTAL_NCE e QUALITY_OF_LIFE são registros das rodadas e podem mencionar instrumentação/testes que não se aplicam ao Current. Este documento descreve o estado final promovido. A falha na biblioteca com updates/DLC NSZ continua pendente; alterações nessa frente não foram incluídas.
