// Maintainer-only resource generation; normal .NET builds need neither Node nor the browser dependencies.
const fs = require('node:fs');
const path = require('node:path');
const locales = path.resolve(__dirname, '../../aihappey-chat/packages/aihappey-i18n/src/locales');
const labels = {
  en: {
    ProvidersDescription: 'Browse {0} AI providers.', SearchProviders: 'Search providers', RefreshProviders: 'Refresh providers',
    ProviderCategory: 'Category', ProviderCountry: 'Country of origin', ProviderInferenceRegions: 'Inference regions',
    ProviderAvailableModelTypes: 'Available model types', ProviderNoDiscoveredModels: 'No models discovered for this provider.',
    ProviderDiscoveryUnavailable: 'The provider catalog is available offline. Model discovery is unavailable; model-type information may be incomplete.',
    ProvidersNoFavorites: 'No favorite providers yet. Use the star on a provider card to save it.',
    ProviderLogoLabel: '{0} logo', ProviderLinkFailed: 'Windows could not open the provider link.', ProviderExperimental: 'Experimental',
    ProviderPricing: 'Pricing', ProviderConsole: 'Console', ProviderDocumentation: 'Documentation', ProviderTerms: 'Terms', ProviderPrivacy: 'Privacy',
    Website: 'Website', Favorites: 'Favorites', Details: 'View details', Close: 'Close'
  },
  nl: {
    ProvidersDescription: 'Bekijk {0} AI-providers.', SearchProviders: 'Providers zoeken', RefreshProviders: 'Providers vernieuwen',
    ProviderCategory: 'Categorie', ProviderCountry: 'Land van herkomst', ProviderInferenceRegions: 'Inferentieregio’s',
    ProviderAvailableModelTypes: 'Beschikbare modeltypen', ProviderNoDiscoveredModels: 'Geen modellen gevonden voor deze provider.',
    ProviderDiscoveryUnavailable: 'De providercatalogus is offline beschikbaar. Modeldetectie is niet beschikbaar; informatie over modeltypen kan onvolledig zijn.',
    ProvidersNoFavorites: 'Nog geen favoriete providers. Gebruik de ster op een providerkaart om deze op te slaan.',
    ProviderLogoLabel: 'Logo van {0}', ProviderLinkFailed: 'Windows kon de providerlink niet openen.', ProviderExperimental: 'Experimenteel',
    ProviderPricing: 'Prijzen', ProviderConsole: 'Console', ProviderDocumentation: 'Documentatie', ProviderTerms: 'Voorwaarden', ProviderPrivacy: 'Privacy',
    Website: 'Website', Favorites: 'Favorieten', Details: 'Details bekijken', Close: 'Sluiten'
  }
};
const escape = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
for (const lang of ['en', 'nl']) {
  const regional = JSON.parse(fs.readFileSync(path.join(locales, lang, 'regional.json'), 'utf8'));
  const main = JSON.parse(fs.readFileSync(path.join(locales, lang, lang + '.json'), 'utf8').replace(/^\uFEFF/, ''));
  const values = { ...labels[lang] };
  for (const [id, value] of Object.entries(main.ai.providerCategories)) values['ProviderCategory_' + id] = value.label;
  for (const [id, value] of Object.entries(regional.countries)) values['ProviderCountry_' + id] = value;
  for (const [id, value] of Object.entries(regional.regions)) values['ProviderRegion_' + id] = value;
  const catalog = path.resolve(__dirname, '../Core/AIHappey.Desktop.Core/ProviderCatalog');
  const entries = JSON.parse(fs.readFileSync(path.join(catalog, 'index.json'), 'utf8'));
  const countryNames = new Intl.DisplayNames([lang], { type: 'region' });
  for (const entry of entries) {
    const provider = JSON.parse(fs.readFileSync(path.join(catalog, entry.file), 'utf8'));
    if (provider.providerCountry) values['ProviderCountry_' + provider.providerCountry] ??= countryNames.of(provider.providerCountry);
    for (const region of provider.inferenceRegions ?? []) values['ProviderRegion_' + region] ??= region;
  }
  const file = path.resolve(__dirname, '../Core/AIHappey.Desktop.Core/Strings', lang, 'Resources.resw');
  const original = fs.readFileSync(file, 'utf8');
  let text = original;
  for (const [name, value] of Object.entries(values)) {
    if (text.includes(`name="${name}"`)) continue;
    text = text.replace('</root>', `  <data name="${name}" xml:space="preserve"><value>${escape(value)}</value></data>\n</root>`);
  }
  if (process.argv.includes('--check')) { if (original !== text) throw new Error('Missing provider resources: ' + file); }
  else if (original !== text) fs.writeFileSync(file, text);
}
console.log('Provider resources verified for en/nl.');
