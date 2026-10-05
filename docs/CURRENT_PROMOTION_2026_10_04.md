# Promoção Current — 04/10/2026

Android 0.0.24 / versionCode 20138. Nome Ryujinx Neo; mainline org.kenjinx.android; assinatura existente preservada. Base: 30ed3aa2a2b65654a7b3148cf3c615fea28e119f, incluindo os dois commits de documentação publicados depois de 783e07fd9.

A promoção retoma 52 arquivos de produção já alterados/criados pelo Claude na Current. Completa DMA de cópia/limpeza pela CPU, fallback de memória global R27/R28, acesso ao estágio de shader e cache 7360/-gmemfb1. Preserva a promoção anterior de NCE, mods por pasta, idiomas por jogo, conversores/cards NSZ/NCZ e comportamento de encerramento. Experimental não é alterado.

Funcional: memfd e janelas físicas; NSO ZBIC; patches distantes; TLS; fallback JIT de 36 bits; espelhamento no gerenciador seguro; settings 12/17; áudio estéreo/preroll/fontes paradas; orçamento e coleta de memória GPU; RAM guard/watchdog de RAM; DMA CPU; flush de uploads; orçamento/filtro/feedback de arrays bindless; deduplicação de descritores; fallback de shaders com ambas as bases de descritores.

Instrumentação excluída: NceTelemetry, GuestHangWatchdog e histórico/diagnósticos HANG1/HANG2; banner de rodadas; logs/estatísticas GPUMEM/ARRAYMEM/ARRAYFB/GMEMFB; contadores de origem/alloc/pico, underruns e janelas/reclamações usados apenas para logs; cronômetros de investigação. Feedback de shader, bytes de GPU, limites, idade/LRU e contadores de entradas ignoradas são estado funcional, não telemetria, e permanecem.

Os saves reconstruídos no Thor não entram no repositório. Não há acesso ao aparelho, instalação, limpeza de dados ou reset de caches nesta promoção. Não há rollback de otimizações aprovadas nem reescrita de TextureGroup/filas/compactação de textura. A opção Eden continua global/por jogo.

O documento do Claude indica manchas no menu do Doom R28 ainda aguardando teste e Power Rangers ainda em investigação. São limites conhecidos; não foram silenciosamente classificados como corrigidos. A autorização atual pede promover o conjunto e preparar a geração/publicação. Validação Experimental relatada não equivale à validação de um APK Current no Thor.

APK, commit e push serão feitos pelo Antigravity com uma única execução de assembleMainlineArmv8Release. Nenhum clean, rerun, publicação nativa separada ou investigação adicional é necessária no prompt.

Verificações desta promoção: 98 verificações de sintaxe C# (desktop/ANDROID), sem erros; compilação C# de LibKenjinx e dependências com ANDROID, sem Native AOT/APK, passou (0 erros, 6 avisos preexistentes). Pacotes preparados somente a partir do cache NuGet local. Os contadores restantes em TextureArrayBudget usados apenas por logs também foram removidos; filtros, pressão, retries e feedback funcional permanecem. A verificação C# foi repetida após essa remoção.
