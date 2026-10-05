package org.kenjinx.android

val SystemLanguage.displayName: String
    get() = when (this) {
        SystemLanguage.Japanese -> "Japonês"
        SystemLanguage.AmericanEnglish -> "Inglês (EUA)"
        SystemLanguage.French -> "Francês"
        SystemLanguage.German -> "Alemão"
        SystemLanguage.Italian -> "Italiano"
        SystemLanguage.Spanish -> "Espanhol (Espanha)"
        SystemLanguage.Chinese -> "Chinês"
        SystemLanguage.Korean -> "Coreano"
        SystemLanguage.Dutch -> "Holandês"
        SystemLanguage.Portuguese -> "Português (Portugal)"
        SystemLanguage.Russian -> "Russo"
        SystemLanguage.Taiwanese -> "Chinês (Taiwan)"
        SystemLanguage.BritishEnglish -> "Inglês (Reino Unido)"
        SystemLanguage.CanadianFrench -> "Francês (Canadá)"
        SystemLanguage.LatinAmericanSpanish -> "Espanhol (América Latina)"
        SystemLanguage.SimplifiedChinese -> "Chinês simplificado"
        SystemLanguage.TraditionalChinese -> "Chinês tradicional"
        SystemLanguage.BrazilianPortuguese -> "Português (Brasil)"
    }
