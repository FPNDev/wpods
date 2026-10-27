/* Hearing Assistance: Hearing Aid, and Media Assist for music and video and for calls. All need hearing set up on an
   iPhone or iPad (0x2C enrolled); until then the note at the bottom says so instead of where the settings come from,
   and the switches are off and disabled. While the AirPods are checked the switches are locked and each group title
   shows the spinner. */
import { createBusy, Note } from '../components/common';
import { createSwitchRow, type SwitchRow } from '../components/switchRow';
import {
  setAttr,
  setChecked,
  setClass,
  setDisabled,
  setHidden,
  setText,
} from '../dom';
import { post } from '../host';
import { type LabelKey, t } from '../i18n';
import { capsOf, current, live, store } from '../state';
import type { Ctx, Page } from './page';

const NOTE_ID = 'hearingNote';

export const createHearing = (ctx: Ctx): Page => {
  const aid = createSwitchRow({
    id: 'h-aid',
    title: 'features.hearingAid',
    desc: null,
    checking: true,
    onChange: (on) => {
      if (store.state) {
        store.state.hearingAidEnabled = on;
      }
      post({ type: 'setHearingAid', value: on });
      ctx.update('features');
    },
  });
  setHidden(aid.desc, true);
  const assistRow = (
    title: LabelKey,
    desc: LabelKey | null,
    field: 'mediaAssistMedia' | 'mediaAssistCalls',
    name: 'media' | 'calls',
  ): SwitchRow => {
    const row = createSwitchRow({
      id: `h-${name}`,
      title,
      desc,
      checking: true,
      onChange: (on) => {
        if (store.state) {
          store.state[field] = on;
        }
        post({ type: 'setMediaAssist', name, value: on });
        ctx.update('features');
      },
    });
    setHidden(row.desc, !desc);
    return row;
  };
  const music = assistRow('hearing.music', null, 'mediaAssistMedia', 'media');
  const calls = assistRow(
    'hearing.calls',
    'hearing.callsDesc',
    'mediaAssistCalls',
    'calls',
  );

  const note = Note({ label: 'features.hearingAidDesc', id: NOTE_ID });
  const noteText = note.lastElementChild as HTMLSpanElement;
  let aidGroup!: HTMLElement;
  let aidTitle!: HTMLHeadingElement;
  let aidList!: HTMLDivElement;
  let assistGroup!: HTMLElement;
  let assistTitle!: HTMLHeadingElement;
  let assistList!: HTMLDivElement;
  const el = (
    <div class="wp-page">
      <section class="wp-group" ref={aidGroup}>
        <h2 class="wp-group__title" ref={aidTitle}>
          <span l:text="features.hearingAid" />
        </h2>
        <div class="wp-list" ref={aidList}>
          {aid.row}
        </div>
      </section>
      <section class="wp-group" ref={assistGroup}>
        <h2 class="wp-group__title" ref={assistTitle}>
          <span l:text="hearing.mediaAssist" />
        </h2>
        <div class="wp-list" ref={assistList}>
          {music.row}
          {calls.row}
        </div>
      </section>
      {note}
    </div>
  ) as HTMLDivElement;

  const aidBusy = createBusy(aidList, []);
  aidTitle.append(aidBusy.spin);
  const assistBusy = createBusy(assistList, []);
  assistTitle.append(assistBusy.spin);

  // value: shown state (remembered while checking); usable: the live state allows a change.
  const renderRow = (
    r: SwitchRow,
    value: boolean | null,
    setUp: boolean,
    usable: boolean,
    block: boolean,
  ): void => {
    r.busy?.set(false, block);
    setClass(r.row, 'is-disabled', !setUp);
    setChecked(r.input, setUp && !!value);
    setDisabled(r.input, !usable);
    setAttr(r.input, 'aria-describedby', setUp ? null : NOTE_ID);
  };

  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
    const pods = st ?? d.saved;
    const checking = !!st && st.checking;
    const block = checking || (!!st && !st.ready);
    const setUp = pods.hearingAidEnrolled !== false;
    const enrolled = !!st && st.hearingAidEnrolled === true;
    setText(noteText, t(setUp ? 'features.hearingAidDesc' : 'hearing.setup'));
    setHidden(aidGroup, !caps.has('hearingAid'));
    setHidden(assistGroup, !caps.has('mediaAssist'));
    aidBusy.set(checking, false);
    assistBusy.set(checking, false);
    renderRow(
      aid,
      pods.hearingAidEnrolled !== null && pods.hearingAidEnabled,
      setUp,
      enrolled,
      block,
    );
    renderRow(
      music,
      pods.mediaAssistMedia,
      setUp,
      enrolled && st.mediaAssistMedia !== null,
      block,
    );
    renderRow(
      calls,
      pods.mediaAssistCalls,
      setUp,
      enrolled && st.mediaAssistCalls !== null,
      block,
    );
  };

  return { el, sections: { features: render } };
};
