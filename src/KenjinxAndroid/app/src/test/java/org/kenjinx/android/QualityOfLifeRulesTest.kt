package org.kenjinx.android

import org.junit.Assert.*
import org.junit.Test
import org.kenjinx.android.cheats.ModFolderRules
import org.kenjinx.android.viewmodels.GameSettingsStore

class QualityOfLifeRulesTest {
    @Test fun languageOverrideUsesEnumNameAndFallsBackToGlobal() {
        val global = SystemLanguage.AmericanEnglish
        assertEquals(SystemLanguage.BrazilianPortuguese, GameSettingsStore.resolveLanguageOverride("BrazilianPortuguese", global))
        listOf("", "999", "UnknownLanguage").forEach {
            assertEquals(global, GameSettingsStore.resolveLanguageOverride(it, global))
        }
        assertTrue(SystemLanguage.entries.all { it.displayName.isNotBlank() })
    }

    @Test fun folderNamesCannotEscapeDestinationAndKeepNormalTranslationNames() {
        listOf("", ".", "..", "../mod", "mod/romfs", "mod\\romfs", "mod\u0000").forEach {
            assertFalse(it, ModFolderRules.isSafeName(it))
        }
        listOf("Tradução PT-BR", "Patch v1..2", "60 FPS").forEach {
            assertTrue(it, ModFolderRules.isSafeName(it))
        }
        assertFalse("Eden-only layout is not supported by the Neo loader", "romfslite" in ModFolderRules.payloadDirectories)
    }
}
