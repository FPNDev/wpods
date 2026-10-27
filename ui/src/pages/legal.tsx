/* Legal: not affiliated with Apple, trademarks, WPods' own features, third-party software, license, no warranty,
   requests. The notices and the license open from the exe folder in the default text viewer (host). Also the
   one-line footer that links here from Home and the No AirPods page. */
import { post } from '../host';
import type { LabelKey } from '../i18n';
import { Icon } from '../icons';
import type { Ctx, Page } from './page';

type LegalFoot = { el: HTMLParagraphElement; link: HTMLButtonElement };

export const LegalFoot = (ctx: Ctx): LegalFoot => {
  let link!: HTMLButtonElement;
  const el = (
    <p class="wp-group__foot wp-legal-foot">
      <span l:text="legal.footer" />{' '}
      <button
        type="button"
        class="wp-link wp-focusable"
        l:text="legal.link"
        ref={link}
        onclick={() => ctx.go('legal')}
      />
    </p>
  ) as HTMLParagraphElement;

  return { el, link };
};

type Open = { label: LabelKey; name: 'license' | 'notices' };

const Section = (p: {
  title: LabelKey;
  body: LabelKey;
  open?: Open;
}): HTMLElement => {
  let list!: HTMLDivElement;
  const el = (
    <section class="wp-group">
      <h2 class="wp-group__title" l:text={p.title} />
      <div class="wp-list" ref={list}>
        <div class="wp-row">
          <span class="wp-row__text" l:text={p.body} />
        </div>
      </div>
    </section>
  ) as HTMLElement;

  if (p.open) {
    const { label, name } = p.open;
    list.append(
      <button
        type="button"
        class="wp-row wp-row--nav"
        onclick={() => post({ type: 'openLegal', name })}
      >
        <span class="wp-row__text">
          <span class="wp-row__title" l:text={label} />
        </span>
        <span class="wp-row__end">
          <Icon name="external" />
        </span>
      </button>,
    );
  }

  return el;
};

export const createLegal = (): Page => {
  const el = (
    <div class="wp-page">
      <section
        class="wp-card wp-disclaimer"
        aria-labelledby="legalTestModeTitle"
      >
        <h2
          class="wp-steps__title"
          id="legalTestModeTitle"
          l:text="legal.testModeTitle"
        />
        <p class="wp-empty__body" l:text="legal.testModeBody" />
      </section>
      <Section title="legal.notAppleTitle" body="legal.notAppleBody" />
      <Section title="legal.trademarksTitle" body="legal.trademarksBody" />
      <Section title="legal.ownFeaturesTitle" body="legal.ownFeaturesBody" />
      <Section
        title="legal.thirdPartyTitle"
        body="legal.thirdPartyBody"
        open={{ label: 'legal.openNotices', name: 'notices' }}
      />
      <Section
        title="legal.licenseTitle"
        body="legal.licenseBody"
        open={{ label: 'legal.openLicense', name: 'license' }}
      />
      <Section title="legal.warrantyTitle" body="legal.warrantyBody" />
      <Section title="legal.contactTitle" body="legal.contactBody" />
    </div>
  ) as HTMLDivElement;

  return { el, sections: {} };
};
