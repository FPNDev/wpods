/* Controls: double-press speed, press-and-hold duration, tone volume (each shown only when supported). */
import { Checking, LockRow } from '../components/common';
import { createSegmented, type Segmented } from '../components/segmented';
import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import type { LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';
import type { HoldDuration, PressSpeed } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Page } from './page';

type SegRow<V extends string> = { row: HTMLDivElement; check: HTMLSpanElement; seg: Segmented<V> };

const segRow = <V extends string>(icon: IconName, title: LabelKey, desc: LabelKey, seg: Segmented<V>): SegRow<V> => {
  const check = Checking();
  const row = (
    <div class="wp-row wp-row--stack">
      <div class="wp-row__head">
        <span class="wp-row__lead"><Icon name={icon} /></span>
        <div class="wp-row__text"><span class="wp-row__title" l:text={title} /><span class="wp-row__desc" l:text={desc} /></div>
        {check}
      </div>
      {seg.el}
    </div>
  ) as HTMLDivElement;
  return { row, check, seg };
};

export const createControls = (): Page => {
  const press = segRow('double', 'controls.pressSpeed', 'controls.pressSpeedDesc', createSegmented<PressSpeed>({
    name: 'pressSpeed',
    legend: 'controls.pressSpeed',
    items: [
      { value: 'default', label: 'controls.default' },
      { value: 'slower', label: 'controls.slower' },
      { value: 'slowest', label: 'controls.slowest' },
    ],
    onChange: (value) => {
      if (store.state) {
        store.state.pressSpeed = value;
      }
      post({ type: 'setPressSpeed', value });
    },
  }));
  const hold = segRow('hold', 'controls.holdDuration', 'controls.holdDurationDesc', createSegmented<HoldDuration>({
    name: 'holdDuration',
    legend: 'controls.holdDuration',
    items: [
      { value: 'default', label: 'controls.default' },
      { value: 'shorter', label: 'controls.shorter' },
      { value: 'shortest', label: 'controls.shortest' },
    ],
    onChange: (value) => {
      if (store.state) {
        store.state.holdDuration = value;
      }
      post({ type: 'setHoldDuration', value });
    },
  }));
  const pressLocked = LockRow('pressLocked');
  const toneLocked = LockRow('toneLocked');
  const toneCheck = Checking();
  let pressGroup!: HTMLElement;
  let toneGroup!: HTMLElement;
  let slider!: HTMLDivElement;
  let tone!: HTMLInputElement;
  let toneValue!: HTMLSpanElement;
  let lastTone: number | null = null;

  const showTone = (value: string): void => {
    tone.style.setProperty('--v', `${value}%`);
    setText(toneValue, value);
  };

  const el = (
    <div class="wp-page">
      <section class="wp-group" ref={pressGroup}>
        <h2 class="wp-group__title" l:text="controls.pressTitle" />
        <div class="wp-list">{pressLocked}{press.row}{hold.row}</div>
      </section>
      <section class="wp-group" ref={toneGroup}>
        <h2 class="wp-group__title" l:text="controls.soundsTitle" />
        <div class="wp-list">
          {toneLocked}
          <div class="wp-row wp-row--stack">
            <div class="wp-row__head">
              <div class="wp-row__text"><span class="wp-row__title" l:text="controls.tone" /><span class="wp-row__desc" l:text="controls.toneDesc" /></div>
              {toneCheck}
            </div>
            <div class="wp-slider" ref={slider}>
              <Icon name="tone" class="wp-slider__icon" />
              <input class="wp-slider__input" type="range" min="0" max="100" l:aria-label="controls.tone" ref={tone}
                oninput={() => showTone(tone.value)}
                onchange={() => {
                  const value = Number(tone.value);
                  lastTone = value;
                  if (store.state) {
                    store.state.toneVolume = value;
                  }
                  post({ type: 'setToneVolume', value });
                }} />
              <span class="wp-slider__value" ref={toneValue} />
            </div>
          </div>
        </div>
      </section>
    </div>
  ) as HTMLDivElement;

  const renderSeg = <V extends string>(r: SegRow<V>, shown: boolean, locked: boolean, checking: boolean, value: V | null): void => {
    setHidden(r.row, !shown);
    setHidden(r.check, !checking);
    setHidden(r.seg.el, checking);
    r.seg.lock(locked, locked ? 'pressLocked' : null);
    r.seg.set(value);
  };

  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
    const pending = !!st && st.checking;
    const hasPress = caps.has('pressSpeed');
    const hasHold = caps.has('holdDuration');
    setHidden(pressGroup, !hasPress && !hasHold);
    setHidden(pressLocked, !!st);
    renderSeg(press, hasPress, !st, pending && st.pressSpeed === null, st?.pressSpeed ?? null);
    renderSeg(hold, hasHold, !st, pending && st.holdDuration === null, st?.holdDuration ?? null);

    setHidden(toneGroup, !caps.has('toneVolume'));
    setHidden(toneLocked, !!st);
    const value = st?.toneVolume ?? null;
    const checking = pending && value === null;
    setHidden(toneCheck, !checking);
    setHidden(slider, checking);
    setDisabled(tone, !st);
    setAttr(tone, 'aria-describedby', st ? null : 'toneLocked');
    if (value !== null && value !== lastTone) {
      lastTone = value;
      tone.value = String(value);
      showTone(tone.value);
    }
    setHidden(toneValue, value === null);
  };

  return { el, sections: { controls: render } };
};
